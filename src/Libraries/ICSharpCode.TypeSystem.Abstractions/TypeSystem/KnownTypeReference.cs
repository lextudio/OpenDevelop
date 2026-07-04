// Copyright (c) 2010-2013 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;

namespace ICSharpCode.TypeSystem
{
	/// <summary>
	/// Represents some well-known types.
	/// </summary>
	public enum KnownTypeCode
	{
		None,
		Object,
		DBNull,
		Boolean,
		Char,
		SByte,
		Byte,
		Int16,
		UInt16,
		Int32,
		UInt32,
		Int64,
		UInt64,
		Single,
		Double,
		Decimal,
		DateTime,
		String = 18,

		Void,
		Type,
		Array,
		Attribute,
		ValueType,
		Enum,
		Delegate,
		MulticastDelegate,
		Exception,
		IntPtr,
		UIntPtr,
		IEnumerable,
		IEnumerator,
		IEnumerableOfT,
		IEnumeratorOfT,
		ICollection,
		ICollectionOfT,
		IList,
		IListOfT,
		IReadOnlyCollectionOfT,
		IReadOnlyListOfT,
		Task,
		TaskOfT,
		NullableOfT,
		IDisposable,
		INotifyCompletion,
		ICriticalNotifyCompletion,
	}

	/// <summary>
	/// Contains well-known type references.
	/// </summary>
	[Serializable]
	public sealed class KnownTypeReference : ITypeReference
	{
		internal const int KnownTypeCodeCount = (int)KnownTypeCode.ICriticalNotifyCompletion + 1;

		static readonly KnownTypeReference[] knownTypeReferences = new KnownTypeReference[KnownTypeCodeCount] {
			null, // None
			new KnownTypeReference(KnownTypeCode.Object,   "System", "Object", baseType: KnownTypeCode.None),
			new KnownTypeReference(KnownTypeCode.DBNull,   "System", "DBNull"),
			new KnownTypeReference(KnownTypeCode.Boolean,  "System", "Boolean",  baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Char,     "System", "Char",     baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.SByte,    "System", "SByte",    baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Byte,     "System", "Byte",     baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Int16,    "System", "Int16",    baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.UInt16,   "System", "UInt16",   baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Int32,    "System", "Int32",    baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.UInt32,   "System", "UInt32",   baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Int64,    "System", "Int64",    baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.UInt64,   "System", "UInt64",   baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Single,   "System", "Single",   baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Double,   "System", "Double",   baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Decimal,  "System", "Decimal",  baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.DateTime, "System", "DateTime", baseType: KnownTypeCode.ValueType),
			null,
			new KnownTypeReference(KnownTypeCode.String,    "System", "String"),
			new KnownTypeReference(KnownTypeCode.Void,      "System", "Void"),
			new KnownTypeReference(KnownTypeCode.Type,      "System", "Type"),
			new KnownTypeReference(KnownTypeCode.Array,     "System", "Array"),
			new KnownTypeReference(KnownTypeCode.Attribute, "System", "Attribute"),
			new KnownTypeReference(KnownTypeCode.ValueType, "System", "ValueType"),
			new KnownTypeReference(KnownTypeCode.Enum,      "System", "Enum", baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.Delegate,  "System", "Delegate"),
			new KnownTypeReference(KnownTypeCode.MulticastDelegate, "System", "MulticastDelegate", baseType: KnownTypeCode.Delegate),
			new KnownTypeReference(KnownTypeCode.Exception, "System", "Exception"),
			new KnownTypeReference(KnownTypeCode.IntPtr,    "System", "IntPtr", baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.UIntPtr,   "System", "UIntPtr", baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.IEnumerable,    "System.Collections", "IEnumerable"),
			new KnownTypeReference(KnownTypeCode.IEnumerator,    "System.Collections", "IEnumerator"),
			new KnownTypeReference(KnownTypeCode.IEnumerableOfT, "System.Collections.Generic", "IEnumerable", 1),
			new KnownTypeReference(KnownTypeCode.IEnumeratorOfT, "System.Collections.Generic", "IEnumerator", 1),
			new KnownTypeReference(KnownTypeCode.ICollection,    "System.Collections", "ICollection"),
			new KnownTypeReference(KnownTypeCode.ICollectionOfT, "System.Collections.Generic", "ICollection", 1),
			new KnownTypeReference(KnownTypeCode.IList,          "System.Collections", "IList"),
			new KnownTypeReference(KnownTypeCode.IListOfT,       "System.Collections.Generic", "IList", 1),
			new KnownTypeReference(KnownTypeCode.IReadOnlyCollectionOfT, "System.Collections.Generic", "IReadOnlyCollection", 1),
			new KnownTypeReference(KnownTypeCode.IReadOnlyListOfT, "System.Collections.Generic", "IReadOnlyList", 1),
			new KnownTypeReference(KnownTypeCode.Task,        "System.Threading.Tasks", "Task"),
			new KnownTypeReference(KnownTypeCode.TaskOfT,     "System.Threading.Tasks", "Task", 1, baseType: KnownTypeCode.Task),
			new KnownTypeReference(KnownTypeCode.NullableOfT, "System", "Nullable", 1, baseType: KnownTypeCode.ValueType),
			new KnownTypeReference(KnownTypeCode.IDisposable, "System", "IDisposable"),
			new KnownTypeReference(KnownTypeCode.INotifyCompletion, "System.Runtime.CompilerServices", "INotifyCompletion"),
			new KnownTypeReference(KnownTypeCode.ICriticalNotifyCompletion, "System.Runtime.CompilerServices", "ICriticalNotifyCompletion"),
		};

		/// <summary>
		/// Gets the known type reference for the specified type code.
		/// </summary>
		public static KnownTypeReference Get(KnownTypeCode typeCode)
		{
			return knownTypeReferences[(int)typeCode];
		}

		public static readonly KnownTypeReference Object = Get(KnownTypeCode.Object);
		public static readonly KnownTypeReference DBNull = Get(KnownTypeCode.DBNull);
		public static readonly KnownTypeReference Boolean = Get(KnownTypeCode.Boolean);
		public static readonly KnownTypeReference Char = Get(KnownTypeCode.Char);
		public static readonly KnownTypeReference SByte = Get(KnownTypeCode.SByte);
		public static readonly KnownTypeReference Byte = Get(KnownTypeCode.Byte);
		public static readonly KnownTypeReference Int16 = Get(KnownTypeCode.Int16);
		public static readonly KnownTypeReference UInt16 = Get(KnownTypeCode.UInt16);
		public static readonly KnownTypeReference Int32 = Get(KnownTypeCode.Int32);
		public static readonly KnownTypeReference UInt32 = Get(KnownTypeCode.UInt32);
		public static readonly KnownTypeReference Int64 = Get(KnownTypeCode.Int64);
		public static readonly KnownTypeReference UInt64 = Get(KnownTypeCode.UInt64);
		public static readonly KnownTypeReference Single = Get(KnownTypeCode.Single);
		public static readonly KnownTypeReference Double = Get(KnownTypeCode.Double);
		public static readonly KnownTypeReference Decimal = Get(KnownTypeCode.Decimal);
		public static readonly KnownTypeReference DateTime = Get(KnownTypeCode.DateTime);
		public static readonly KnownTypeReference String = Get(KnownTypeCode.String);
		public static readonly KnownTypeReference Void = Get(KnownTypeCode.Void);
		public static readonly KnownTypeReference Type = Get(KnownTypeCode.Type);
		public static readonly KnownTypeReference Array = Get(KnownTypeCode.Array);
		public static readonly KnownTypeReference Attribute = Get(KnownTypeCode.Attribute);
		public static readonly KnownTypeReference ValueType = Get(KnownTypeCode.ValueType);
		public static readonly KnownTypeReference Enum = Get(KnownTypeCode.Enum);
		public static readonly KnownTypeReference Delegate = Get(KnownTypeCode.Delegate);
		public static readonly KnownTypeReference MulticastDelegate = Get(KnownTypeCode.MulticastDelegate);
		public static readonly KnownTypeReference Exception = Get(KnownTypeCode.Exception);
		public static readonly KnownTypeReference IntPtr = Get(KnownTypeCode.IntPtr);
		public static readonly KnownTypeReference UIntPtr = Get(KnownTypeCode.UIntPtr);
		public static readonly KnownTypeReference IEnumerable = Get(KnownTypeCode.IEnumerable);
		public static readonly KnownTypeReference IEnumerator = Get(KnownTypeCode.IEnumerator);
		public static readonly KnownTypeReference IEnumerableOfT = Get(KnownTypeCode.IEnumerableOfT);
		public static readonly KnownTypeReference IEnumeratorOfT = Get(KnownTypeCode.IEnumeratorOfT);
		public static readonly KnownTypeReference ICollection = Get(KnownTypeCode.ICollection);
		public static readonly KnownTypeReference ICollectionOfT = Get(KnownTypeCode.ICollectionOfT);
		public static readonly KnownTypeReference IList = Get(KnownTypeCode.IList);
		public static readonly KnownTypeReference IListOfT = Get(KnownTypeCode.IListOfT);
		public static readonly KnownTypeReference IReadOnlyCollectionOfT = Get(KnownTypeCode.IReadOnlyCollectionOfT);
		public static readonly KnownTypeReference IReadOnlyListOfT = Get(KnownTypeCode.IReadOnlyListOfT);
		public static readonly KnownTypeReference Task = Get(KnownTypeCode.Task);
		public static readonly KnownTypeReference TaskOfT = Get(KnownTypeCode.TaskOfT);
		public static readonly KnownTypeReference NullableOfT = Get(KnownTypeCode.NullableOfT);
		public static readonly KnownTypeReference IDisposable = Get(KnownTypeCode.IDisposable);
		public static readonly KnownTypeReference INotifyCompletion = Get(KnownTypeCode.INotifyCompletion);
		public static readonly KnownTypeReference ICriticalNotifyCompletion = Get(KnownTypeCode.ICriticalNotifyCompletion);

		readonly KnownTypeCode knownTypeCode;
		readonly string namespaceName;
		readonly string name;
		readonly int typeParameterCount;
		internal readonly KnownTypeCode baseType;

		private KnownTypeReference(KnownTypeCode knownTypeCode, string namespaceName, string name, int typeParameterCount = 0, KnownTypeCode baseType = KnownTypeCode.Object)
		{
			this.knownTypeCode = knownTypeCode;
			this.namespaceName = namespaceName;
			this.name = name;
			this.typeParameterCount = typeParameterCount;
			this.baseType = baseType;
		}

		public KnownTypeCode KnownTypeCode {
			get { return knownTypeCode; }
		}

		public string Namespace {
			get { return namespaceName; }
		}

		public string Name {
			get { return name; }
		}

		public int TypeParameterCount {
			get { return typeParameterCount; }
		}

		public IType Resolve(ITypeResolveContext context)
		{
			return context.Compilation.FindType(knownTypeCode);
		}

		public override string ToString()
		{
			return GetCSharpNameByTypeCode(knownTypeCode) ?? (this.Namespace + "." + this.Name);
		}

		/// <summary>
		/// Gets the C# primitive type name from the known type code.
		/// </summary>
		public static string GetCSharpNameByTypeCode(KnownTypeCode knownTypeCode)
		{
			switch (knownTypeCode) {
				case KnownTypeCode.Object:
					return "object";
				case KnownTypeCode.Boolean:
					return "bool";
				case KnownTypeCode.Char:
					return "char";
				case KnownTypeCode.SByte:
					return "sbyte";
				case KnownTypeCode.Byte:
					return "byte";
				case KnownTypeCode.Int16:
					return "short";
				case KnownTypeCode.UInt16:
					return "ushort";
				case KnownTypeCode.Int32:
					return "int";
				case KnownTypeCode.UInt32:
					return "uint";
				case KnownTypeCode.Int64:
					return "long";
				case KnownTypeCode.UInt64:
					return "ulong";
				case KnownTypeCode.Single:
					return "float";
				case KnownTypeCode.Double:
					return "double";
				case KnownTypeCode.Decimal:
					return "decimal";
				case KnownTypeCode.String:
					return "string";
				case KnownTypeCode.Void:
					return "void";
				default:
					return null;
			}
		}
	}
}
