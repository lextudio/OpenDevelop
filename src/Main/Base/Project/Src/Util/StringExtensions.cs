#if HAS_UNO
namespace ICSharpCode.SharpDevelop
{
	internal static class StringExtensions
	{
		public static int GetStableHashCode(this string str)
		{
			unchecked
			{
				int hash = 5381;
				foreach (char c in str)
					hash = ((hash << 5) + hash) ^ c;
				return hash;
			}
		}
	}
}
#endif
