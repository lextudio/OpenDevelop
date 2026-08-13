using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

using ICSharpCode.Core;
using ICSharpCode.FormsDesigner;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpBinding.FormsDesigner
{
	/// <summary>
	/// WinForms loader whose persistent model is Roslyn syntax plus the live component graph.
	/// It deliberately does not use CodeDomDesignerLoader, CodeDomSerializer, or System.CodeDom.
	/// </summary>
	public sealed class RoslynDesignerLoader : BasicDesignerLoader
	{
		static readonly string[] PreferredProperties = {
			"AutoScaleDimensions", "AutoScaleMode", "BackColor", "ClientSize", "Dock", "Enabled",
			"Font", "ForeColor", "Location", "MaximumSize", "MinimumSize", "Name", "Padding",
			"Size", "TabIndex", "Text", "Visible"
		};

		readonly FormsDesignerViewContent viewContent;
		readonly Dictionary<string, IComponent> components = new(StringComparer.Ordinal);
		ITypeResolutionService typeResolution;
		ClassDeclarationSyntax designerClass;
		MethodDeclarationSyntax initializeComponent;
		CompilationUnitSyntax parsedRoot;
		string designerClassName;
		RoslynDesignerResourceModel resources;

		public RoslynDesignerLoader(FormsDesignerViewContent viewContent) => this.viewContent = viewContent;

		protected override void Initialize()
		{
			base.Initialize();
			typeResolution = (ITypeResolutionService)LoaderHost.GetService(typeof(ITypeResolutionService));
			var changes = (IComponentChangeService)LoaderHost.GetService(typeof(IComponentChangeService));
			if (changes != null) changes.ComponentAdded += OnComponentAdded;
			EnableComponentNotification(true);
		}

		public override void Dispose()
		{
			var changes = LoaderHost?.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
			if (changes != null) changes.ComponentAdded -= OnComponentAdded;
			base.Dispose();
		}

		protected override void PerformLoad(IDesignerSerializationManager manager)
		{
			try {
				ParseSource();
				resources = new RoslynDesignerResourceModel(LoaderHost);
				SetBaseComponentClassName(designerClassName);
				var rootType = ResolveRootType();
				var root = LoaderHost.CreateComponent(rootType, designerClassName);
				components[designerClassName] = root;
				components["this"] = root;
				ConfigureDesignerControl(root);

				foreach (var statement in initializeComponent.Body?.Statements ?? default)
					Execute(statement);
			} catch (Exception ex) {
				LoggingService.Error("Roslyn WinForms designer load failed", ex);
				throw new FormsDesignerLoadException("Roslyn WinForms designer could not load InitializeComponent: " + ex.Message, ex);
			}
		}

		protected override void PerformFlush(IDesignerSerializationManager manager)
		{
			try {
				RewriteSource(manager);
			} catch (Exception ex) {
				throw new FormsDesignerLoadException("Roslyn WinForms designer could not save InitializeComponent: " + ex.Message, ex);
			}
		}

		void ParseSource()
		{
			var path = viewContent.DesignerCodeFile.FileName;
			parsedRoot = CSharpSyntaxTree.ParseText(viewContent.DesignerCodeFileDocument.Text, path: path).GetCompilationUnitRoot();
			designerClass = FindClass(parsedRoot, out initializeComponent);
			if (designerClass == null && viewContent.DesignerCodeFile != viewContent.PrimaryFile) {
				parsedRoot = CSharpSyntaxTree.ParseText(viewContent.PrimaryFileContent.Text, path: viewContent.PrimaryFileName).GetCompilationUnitRoot();
				designerClass = FindClass(parsedRoot, out initializeComponent);
			}
			if (designerClass == null || initializeComponent?.Body == null)
				throw new FormsDesignerLoadException("The InitializeComponent method was not found.");
			designerClassName = designerClass.Identifier.ValueText;
		}

		static ClassDeclarationSyntax FindClass(SyntaxNode root, out MethodDeclarationSyntax method)
		{
			foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>()) {
				method = type.Members.OfType<MethodDeclarationSyntax>()
					.FirstOrDefault(m => m.Identifier.ValueText == "InitializeComponent" && m.ParameterList.Parameters.Count == 0);
				if (method != null) return type;
			}
			method = null;
			return null;
		}

		Type ResolveRootType()
		{
			var primary = CSharpSyntaxTree.ParseText(viewContent.PrimaryFileContent.Text).GetCompilationUnitRoot();
			var declaration = primary.DescendantNodes().OfType<ClassDeclarationSyntax>()
				.FirstOrDefault(c => c.Identifier.ValueText == designerClassName && c.BaseList != null);
			var typeName = declaration?.BaseList?.Types.FirstOrDefault()?.Type.ToString() ?? "System.Windows.Forms.Form";
			return ResolveType(typeName) ?? typeof(System.Windows.Forms.Form);
		}

		Type ResolveType(string name)
		{
			var aliases = new Dictionary<string, Type>(StringComparer.Ordinal) {
				["Form"] = typeof(System.Windows.Forms.Form), ["Panel"] = typeof(System.Windows.Forms.Panel),
				["Button"] = typeof(System.Windows.Forms.Button), ["Label"] = typeof(System.Windows.Forms.Label),
				["TextBox"] = typeof(System.Windows.Forms.TextBox), ["NumericUpDown"] = typeof(System.Windows.Forms.NumericUpDown)
			};
			if (aliases.TryGetValue(name, out var alias)) return alias;
			var runtimeType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
			if (runtimeType != null) return runtimeType;
			try { return typeResolution?.GetType(name, false) ?? Type.GetType(name, false); } catch { return Type.GetType(name, false); }
		}

		void Execute(StatementSyntax statement)
		{
			if (statement is LocalDeclarationStatementSyntax local) {
				foreach (var variable in local.Declaration.Variables) {
					if (variable.Initializer == null) continue;
					var value = Evaluate(variable.Initializer.Value) as IComponent;
					if (value != null) components[variable.Identifier.ValueText] = value;
				}
				return;
			}
			if (statement is not ExpressionStatementSyntax expression) return;
			if (expression.Expression is AssignmentExpressionSyntax assignment) { ExecuteAssignment(assignment); return; }
			if (expression.Expression is InvocationExpressionSyntax invocation) { ExecuteInvocation(invocation); return; }
		}

		void ExecuteAssignment(AssignmentExpressionSyntax assignment)
		{
			if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return; // event hookup is source-owned and retained separately later
			var target = StripThis(assignment.Left.ToString());
			if (!target.Contains('.')) {
				var rootProperty = TypeDescriptor.GetProperties(components["this"])[target];
				if (rootProperty != null && !rootProperty.IsReadOnly) {
					rootProperty.SetValue(components["this"], ConvertValue(Evaluate(assignment.Right), rootProperty.PropertyType));
					return;
				}
				if (assignment.Right is ObjectCreationExpressionSyntax creation) {
					var type = ResolveType(creation.Type.ToString()) ?? throw new InvalidOperationException("Cannot resolve " + creation.Type);
					var component = LoaderHost.CreateComponent(type, target);
					components[target] = component;
					ConfigureDesignerControl(component);
				}
				return;
			}
			var split = target.LastIndexOf('.');
			var owner = ResolveObject(target[..split]);
			var propertyName = target[(split + 1)..];
			if (owner == null) return;
			var property = TypeDescriptor.GetProperties(owner)[propertyName];
			if (property != null && !property.IsReadOnly)
				property.SetValue(owner, ConvertValue(Evaluate(assignment.Right), property.PropertyType));
		}

		void ExecuteInvocation(InvocationExpressionSyntax invocation)
		{
			if (invocation.Expression is not MemberAccessExpressionSyntax member) return;
			var targetText = StripThis(member.Expression.ToString());
			if (member.Name.Identifier.ValueText == "ApplyResources" && invocation.ArgumentList.Arguments.Count >= 2) {
				var component = Evaluate(invocation.ArgumentList.Arguments[0].Expression) as IComponent;
				var resourceName = Evaluate(invocation.ArgumentList.Arguments[1].Expression) as string;
				if (component != null && resourceName != null) resources.Apply(component, resourceName);
				return;
			}
			if (member.Name.Identifier.ValueText == "Add" && (targetText == "Controls" || targetText.EndsWith(".Controls", StringComparison.Ordinal))) {
				var parentPath = targetText == "Controls" ? "" : targetText[..^".Controls".Length];
				var parent = (parentPath.Length == 0 ? components["this"] : ResolveObject(parentPath)) as System.Windows.Forms.Control;
				var child = Evaluate(invocation.ArgumentList.Arguments[0].Expression) as System.Windows.Forms.Control;
				if (parent != null && child != null) parent.Controls.Add(child);
				return;
			}
			var target = ResolveObject(targetText);
			var args = invocation.ArgumentList.Arguments.Select(a => Evaluate(a.Expression)).ToArray();
			var method = target?.GetType().GetMethods().FirstOrDefault(m => m.Name == member.Name.Identifier.ValueText && m.GetParameters().Length == args.Length);
			try { method?.Invoke(target, args); } catch (TargetInvocationException ex) { LoggingService.Warn("Designer call ignored: " + ex.InnerException?.Message); }
		}

		object Evaluate(ExpressionSyntax expression)
		{
			switch (expression) {
				case LiteralExpressionSyntax literal: return literal.Token.Value;
				case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression): return Negate(Evaluate(unary.Operand));
				case ThisExpressionSyntax: return components["this"];
				case TypeOfExpressionSyntax typeOf: return ResolveType(typeOf.Type.ToString());
				case IdentifierNameSyntax identifier: return ResolveObject(identifier.Identifier.ValueText) ?? ResolveType(identifier.Identifier.ValueText);
				case CastExpressionSyntax cast: return ConvertValue(Evaluate(cast.Expression), ResolveType(cast.Type.ToString()));
				case ParenthesizedExpressionSyntax parenthesized: return Evaluate(parenthesized.Expression);
				case MemberAccessExpressionSyntax access:
					if (access.Expression is ThisExpressionSyntax && components.TryGetValue(access.Name.Identifier.ValueText, out var fieldComponent))
						return fieldComponent;
					var owner = Evaluate(access.Expression);
					if (owner is Type type) return type.GetField(access.Name.Identifier.ValueText)?.GetValue(null) ?? Enum.Parse(type, access.Name.Identifier.ValueText);
					return TypeDescriptor.GetProperties(owner)[access.Name.Identifier.ValueText]?.GetValue(owner);
				case ObjectCreationExpressionSyntax creation:
					var creationType = ResolveType(creation.Type.ToString()) ?? throw new InvalidOperationException("Cannot resolve " + creation.Type);
					var args = creation.ArgumentList?.Arguments.Select(a => Evaluate(a.Expression)).ToArray() ?? Array.Empty<object>();
					return Activator.CreateInstance(creationType, args);
				default: throw new NotSupportedException("Unsupported designer expression: " + expression);
			}
		}

		object ResolveObject(string path)
		{
			path = StripThis(path);
			if (components.TryGetValue(path, out var component)) return component;
			return ResolveType(path);
		}

		static string StripThis(string value) => value.StartsWith("this.", StringComparison.Ordinal) ? value[5..] : value;
		static object Negate(object value) => value switch { int v => -v, long v => -v, float v => -v, double v => -v, decimal v => -v, _ => value };
		static object ConvertValue(object value, Type target) => value == null || target == null || target.IsInstanceOfType(value) ? value : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);

		void RewriteSource(IDesignerSerializationManager manager)
		{
			ParseSource();
			var rootComponent = LoaderHost.RootComponent as IComponent;
			var all = LoaderHost.Container.Components.Cast<IComponent>().ToList();
			var controls = all.Where(c => !ReferenceEquals(c, rootComponent)).ToList();
			var legacyAdapter = new LegacyCodeDomSerializerAdapter(manager);
			var statements = new List<StatementSyntax>();
			var resourceStatements = initializeComponent.Body.Statements.Where(IsResourceStatement).ToList();
			var localized = resourceStatements.Count > 0;
			statements.AddRange(resourceStatements.OfType<LocalDeclarationStatementSyntax>());

			foreach (var component in controls) {
				var name = component.Site?.Name;
				statements.Add(Assign(SyntaxFactory.IdentifierName(name), SyntaxFactory.ObjectCreationExpression(TypeName(component.GetType())).WithArgumentList(SyntaxFactory.ArgumentList())));
			}
			statements.Add(Call(null, "SuspendLayout"));
			if (localized) {
				foreach (var component in all) resources.Write(component, ReferenceEquals(component, rootComponent) ? "$this" : component.Site.Name);
				statements.AddRange(resourceStatements.OfType<ExpressionStatementSyntax>());
			} else {
				foreach (var component in controls) {
					if (legacyAdapter.IsRequired(component.GetType()))
						statements.AddRange(legacyAdapter.Serialize(component));
					else
						AddPropertyStatements(statements, component);
				}
				AddPropertyStatements(statements, rootComponent);
			}
			foreach (var eventStatement in initializeComponent.Body.Statements
				.OfType<ExpressionStatementSyntax>()
				.Where(s => s.Expression is AssignmentExpressionSyntax a && (a.IsKind(SyntaxKind.AddAssignmentExpression) || a.IsKind(SyntaxKind.SubtractAssignmentExpression))))
				statements.Add(ModernizeEvent(eventStatement));

			foreach (var child in controls.OfType<System.Windows.Forms.Control>()) {
				var parent = child.Parent;
				if (parent == null) continue;
				var parentExpression = ReferenceEquals(parent, rootComponent) ? null : SyntaxFactory.IdentifierName(parent.Site?.Name);
				var controlsExpression = parentExpression == null
					? (ExpressionSyntax)SyntaxFactory.IdentifierName("Controls")
					: SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, parentExpression, SyntaxFactory.IdentifierName("Controls"));
				statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
					SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, controlsExpression, SyntaxFactory.IdentifierName("Add")),
					SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(child.Site.Name)))))));
			}
			statements.Add(Call(null, "ResumeLayout", SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)));

			var annotation = new SyntaxAnnotation("FormsDesignerOwned");
			var method = initializeComponent.WithBody(SyntaxFactory.Block(statements)).WithAdditionalAnnotations(annotation);
			var edited = parsedRoot.ReplaceNode(initializeComponent, method);
			var currentClass = edited.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.ValueText == designerClassName);
			var generatedNames = controls.Select(c => c.Site.Name).ToHashSet(StringComparer.Ordinal);
			var oldDesignerNames = initializeComponent.Body.Statements.OfType<ExpressionStatementSyntax>()
				.Select(s => s.Expression as AssignmentExpressionSyntax)
				.Where(a => a?.Right is ObjectCreationExpressionSyntax)
				.Select(a => StripThis(a.Left.ToString())).Where(n => !n.Contains('.')).ToHashSet(StringComparer.Ordinal);
			var ownedNames = generatedNames.Concat(oldDesignerNames).ToHashSet(StringComparer.Ordinal);
			var retained = currentClass.Members.Where(m => m is not FieldDeclarationSyntax f || !f.Declaration.Variables.Any(v => ownedNames.Contains(v.Identifier.ValueText))).ToList();
			retained.AddRange(controls.Select(c => SyntaxFactory.FieldDeclaration(
				SyntaxFactory.VariableDeclaration(TypeName(c.GetType())).AddVariables(SyntaxFactory.VariableDeclarator(c.Site.Name)))
				.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))).WithAdditionalAnnotations(annotation)));
			edited = edited.ReplaceNode(currentClass, currentClass.WithMembers(SyntaxFactory.List(retained)));

			using var workspace = new AdhocWorkspace();
			var languageService = SD.Services.GetService<LanguageServiceRegistry>()?.GetService(viewContent.DesignerCodeFile.FileName) as CSharpVBLanguageService;
			var projectDocument = languageService?.TryGetProjectDocument(viewContent.DesignerCodeFile.FileName);
			var formatted = projectDocument != null
				? Formatter.Format(edited, annotation, projectDocument.Project.Solution.Workspace, projectDocument.Project.Solution.Workspace.Options)
				: Formatter.Format(edited, annotation, workspace);
			var text = formatted.ToFullString();
			File.WriteAllText(viewContent.DesignerCodeFile.FileName, text);
			viewContent.DesignerCodeFileContent = text;
		}

		void AddPropertyStatements(List<StatementSyntax> statements, IComponent component)
		{
			if (component == null) return;
			var target = ReferenceEquals(component, LoaderHost.RootComponent) ? null : SyntaxFactory.IdentifierName(component.Site.Name);
			foreach (var name in PreferredProperties) {
				var property = TypeDescriptor.GetProperties(component)[name];
				if (property == null || property.IsReadOnly || !property.ShouldSerializeValue(component)) continue;
				if (!TryExpression(property.GetValue(component), out var value)) continue;
				var left = target == null ? (ExpressionSyntax)SyntaxFactory.IdentifierName(name) :
					SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, target, SyntaxFactory.IdentifierName(name));
				statements.Add(Assign(left, value));
			}
		}

		static bool TryExpression(object value, out ExpressionSyntax expression)
		{
			if (value == null) { expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression); return true; }
			if (value is string text) { expression = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(text)); return true; }
			if (value is bool boolean) { expression = SyntaxFactory.LiteralExpression(boolean ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression); return true; }
			if (value is int integer) { expression = SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(integer)); return true; }
			if (value is float single) { expression = SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(single)); return true; }
			if (value.GetType().IsEnum) { expression = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, TypeName(value.GetType()), SyntaxFactory.IdentifierName(value.ToString())); return true; }
			if (value is Point point) return Constructor(value.GetType(), new object[] { point.X, point.Y }, out expression);
			if (value is Size size) return Constructor(value.GetType(), new object[] { size.Width, size.Height }, out expression);
			if (value is SizeF sizeF) return Constructor(value.GetType(), new object[] { sizeF.Width, sizeF.Height }, out expression);
			expression = null; return false;
		}

		static bool Constructor(Type type, object[] values, out ExpressionSyntax expression)
		{
			var args = new List<ArgumentSyntax>();
			foreach (var value in values) { if (!TryExpression(value, out var item)) { expression = null; return false; } args.Add(SyntaxFactory.Argument(item)); }
			expression = SyntaxFactory.ObjectCreationExpression(TypeName(type)).WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));
			return true;
		}

		static TypeSyntax TypeName(Type type) => SyntaxFactory.ParseTypeName(type.FullName.Replace('+', '.'));
		static StatementSyntax Assign(ExpressionSyntax left, ExpressionSyntax right) => SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, left, right));
		static StatementSyntax Call(ExpressionSyntax target, string name, params ExpressionSyntax[] args) {
			ExpressionSyntax method = target == null ? SyntaxFactory.IdentifierName(name) : SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, target, SyntaxFactory.IdentifierName(name));
			return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(method, SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args.Select(SyntaxFactory.Argument)))));
		}

		static ExpressionStatementSyntax ModernizeEvent(ExpressionStatementSyntax statement)
		{
			var assignment = (AssignmentExpressionSyntax)statement.Expression;
			ExpressionSyntax left = assignment.Left;
			if (left is MemberAccessExpressionSyntax leftMember && leftMember.Expression is ThisExpressionSyntax)
				left = SyntaxFactory.IdentifierName(leftMember.Name.Identifier.ValueText);
			ExpressionSyntax right = assignment.Right;
			if (right is ObjectCreationExpressionSyntax creation && creation.ArgumentList?.Arguments.Count == 1)
				right = creation.ArgumentList.Arguments[0].Expression;
			if (right is MemberAccessExpressionSyntax rightMember && rightMember.Expression is ThisExpressionSyntax)
				right = SyntaxFactory.IdentifierName(rightMember.Name.Identifier.ValueText);
			return statement.WithExpression(assignment.WithLeft(left).WithRight(right));
		}

		static bool IsResourceStatement(StatementSyntax statement)
		{
			if (statement is LocalDeclarationStatementSyntax local)
				return local.Declaration.Type.ToString().Contains("ComponentResourceManager", StringComparison.Ordinal);
			return statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } }
				&& member.Name.Identifier.ValueText == "ApplyResources";
		}

		static void OnComponentAdded(object sender, ComponentEventArgs e) => ConfigureDesignerControl(e.Component);
		static void ConfigureDesignerControl(IComponent component)
		{
			if (component is not System.Windows.Forms.Control control) return;
			control.AllowDrop = true;
			control.DragEnter -= OnDragEnter; control.DragEnter += OnDragEnter;
			control.DragDrop -= OnDragDrop; control.DragDrop += OnDragDrop;
		}
		static void OnDragEnter(object sender, System.Windows.Forms.DragEventArgs e) { if (e.Data.GetDataPresent(typeof(System.Drawing.Design.ToolboxItem))) e.Effect = System.Windows.Forms.DragDropEffects.Copy; }
		static void OnDragDrop(object sender, System.Windows.Forms.DragEventArgs e)
		{
			if (sender is not System.Windows.Forms.Control target || e.Data.GetData(typeof(System.Drawing.Design.ToolboxItem)) is not System.Drawing.Design.ToolboxItem item) return;
			var host = target.Site?.GetService(typeof(IDesignerHost)) as IDesignerHost;
			if (host == null || host.GetDesigner(host.RootComponent) is not System.Drawing.Design.IToolboxUser user || !user.GetToolSupported(item)) return;
			(host.GetService(typeof(ISelectionService)) as ISelectionService)?.SetSelectedComponents(new object[] { target }, SelectionTypes.Replace);
			var before = host.Container.Components.Cast<IComponent>().ToHashSet(); user.ToolPicked(item);
			foreach (var added in host.Container.Components.Cast<IComponent>().Where(c => !before.Contains(c)).OfType<System.Windows.Forms.Control>()) {
				if (added.Size.IsEmpty && added is System.Windows.Forms.NumericUpDown) added.Size = new Size(120, 20);
				added.Location = target.PointToClient(new Point(e.X, e.Y));
			}
		}
	}
}
