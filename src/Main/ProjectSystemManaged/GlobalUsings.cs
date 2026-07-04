// Additional global usings for upstream MIT-linked files that assume
// System.Collections.Immutable is in scope.
global using System.Collections.Immutable;
global using Microsoft.VisualStudio.Composition;
global using Microsoft.VisualStudio.Imaging;
// System.Composition.ImportingConstructorAttribute is sealed, so MefAttributes.cs can't
// subclass it under the Microsoft.VisualStudio.Composition namespace like Export/Import/ImportMany.
// Alias the bare name directly to the real (sealed) type so real VS MEF's constructor-selection
// logic recognizes [ImportingConstructor] on the linked upstream files. See docs/project-system.md
// (Slice 44).
global using ImportingConstructorAttribute = System.Composition.ImportingConstructorAttribute;
