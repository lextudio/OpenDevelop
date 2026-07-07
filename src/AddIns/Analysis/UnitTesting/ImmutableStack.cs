using System.Collections.Generic;

namespace ICSharpCode.UnitTesting
{
	public sealed class ImmutableStack<T> : IEnumerable<T>
	{
		static readonly ImmutableStack<T> empty = new ImmutableStack<T>(null, default(T), true);
		readonly T value;
		readonly ImmutableStack<T> previous;

		ImmutableStack(ImmutableStack<T> previous, T value, bool isEmpty)
		{
			this.previous = previous;
			this.value = value;
			this.IsEmpty = isEmpty;
		}

		public bool IsEmpty { get; }

		public static ImmutableStack<T> Empty {
			get { return empty; }
		}

		public ImmutableStack<T> Push(T item)
		{
			return new ImmutableStack<T>(this, item, false);
		}

		public T Peek()
		{
			return value;
		}

		public IEnumerator<T> GetEnumerator()
		{
			for (ImmutableStack<T> s = this; !s.IsEmpty; s = s.previous)
				yield return s.value;
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}
	}
}
