using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>One repository's raw release list as last fetched from GitHub.</summary>
	public sealed class CachedReleaseList
	{
		public string ETag { get; set; }
		public DateTimeOffset FetchedAt { get; set; }
		public string Body { get; set; }
	}

	/// <summary>
	/// On-disk cache of each trusted repository's release list, so the 60 requests/hour of unauthenticated
	/// GitHub access are spent at most once per repository per day. A cache that cannot be read or written
	/// is never an error: the manager just asks GitHub again.
	/// </summary>
	public sealed class ReleaseCache
	{
		readonly string directory;

		public ReleaseCache(string directory)
		{
			this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
		}

		string FileFor(RepositoryReference repository)
		{
			// Names are validated by RepositoryReference (letters, digits, '.', '_', '-'), so this is a safe file name.
			return Path.Combine(directory, (repository.Owner + "__" + repository.Name).ToLowerInvariant() + ".json");
		}

		public CachedReleaseList TryRead(RepositoryReference repository)
		{
			try {
				string file = FileFor(repository);
				if (!File.Exists(file))
					return null;
				var cached = JsonSerializer.Deserialize<CachedReleaseList>(File.ReadAllText(file, Encoding.UTF8));
				return cached != null && !string.IsNullOrEmpty(cached.Body) ? cached : null;
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) {
				return null;
			}
		}

		public void Write(RepositoryReference repository, CachedReleaseList list)
		{
			try {
				Directory.CreateDirectory(directory);
				string file = FileFor(repository);
				string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
				File.WriteAllText(temp, JsonSerializer.Serialize(list), new UTF8Encoding(false));
				File.Move(temp, file, true);
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				// Not caching only costs an extra request later.
			}
		}
	}
}
