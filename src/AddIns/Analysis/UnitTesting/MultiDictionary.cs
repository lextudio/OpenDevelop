using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.UnitTesting.Frameworks
{
	sealed class MultiDictionary<TKey, TValue> : IEnumerable<IGrouping<TKey, TValue>>
	{
		Dictionary<TKey, List<TValue>> dict = new Dictionary<TKey, List<TValue>>();

		public void Add(TKey key, TValue value)
		{
			if (!dict.TryGetValue(key, out var list))
			{
				list = new List<TValue>();
				dict[key] = list;
			}
			list.Add(value);
		}

		public int Count => dict.Count;

		public IEnumerable<TKey> Keys => dict.Keys;

		public IEnumerable<TValue> Values => dict.Values.SelectMany(v => v);

		public IEnumerator<IGrouping<TKey, TValue>> GetEnumerator()
		{
			return dict.Select(kvp => (IGrouping<TKey, TValue>)new Grouping(kvp.Key, kvp.Value)).GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		class Grouping : IGrouping<TKey, TValue>
		{
			readonly TKey key;
			readonly List<TValue> values;

			public Grouping(TKey key, List<TValue> values)
			{
				this.key = key;
				this.values = values;
			}

			public TKey Key => key;

			public IEnumerator<TValue> GetEnumerator()
			{
				return values.GetEnumerator();
			}

			IEnumerator IEnumerable.GetEnumerator()
			{
				return GetEnumerator();
			}
		}
	}
}
