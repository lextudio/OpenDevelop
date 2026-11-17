using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>
	/// Reading and unpacking a <c>.odaddin</c> (a zip: one <c>.addin</c> manifest at the root plus the
	/// add-in's files). The package is untrusted input until the user has decided to install it, so
	/// nothing here trusts a name, a path or a size inside it.
	/// </summary>
	public static class OdAddinPackage
	{
		/// <summary>Manifest attribute recording the <c>owner/repo</c> an add-in was installed from.</summary>
		public const string RepositoryManifestAttribute = "__odRepository";

		/// <summary>Manifest attribute recording the release tag an add-in was installed from.</summary>
		public const string ReleaseManifestAttribute = "__odRelease";

		/// <summary>The most an unpacked package may occupy.</summary>
		public const long MaxUncompressedBytes = 2L << 30;

		public const int MaxEntries = 20000;

		/// <summary>The most a manifest may occupy.</summary>
		public const int MaxManifestBytes = 1 << 20;

		static readonly Regex IdentityPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.CultureInvariant);

		/// <summary>
		/// Whether an add-in identity can be used as a directory name: it becomes the install folder, which
		/// is created and, on update, deleted recursively, so it must not be able to name anything else.
		/// </summary>
		public static bool IsSafeIdentity(string identity)
		{
			return identity != null
				&& IdentityPattern.IsMatch(identity)
				&& !identity.EndsWith(".", StringComparison.Ordinal)
				&& !identity.Contains("..", StringComparison.Ordinal);
		}

		/// <summary>The single <c>.addin</c> file at the top level of an unpacked package (extension compared case-insensitively, as in <see cref="ReadManifestText"/>); null when there is none or several.</summary>
		public static FileInfo FindUnpackedManifest(string directory)
		{
			var manifests = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
				.Where(f => f.Name.EndsWith(".addin", StringComparison.OrdinalIgnoreCase))
				.Take(2)
				.ToList();
			return manifests.Count == 1 ? manifests[0] : null;
		}

		/// <summary>The text of the package's single root <c>.addin</c> manifest.</summary>
		/// <exception cref="InvalidDataException">The file is not a zip, or has no or several root manifests.</exception>
		public static string ReadManifestText(string packageFile)
		{
			try {
				using (ZipArchive zip = ZipFile.OpenRead(packageFile)) {
					var manifests = zip.Entries
						.Where(e => e.FullName.IndexOf('/') < 0 && e.FullName.IndexOf('\\') < 0
							&& e.FullName.EndsWith(".addin", StringComparison.OrdinalIgnoreCase))
						.ToList();
					if (manifests.Count == 0)
						throw new InvalidDataException("The package has no .addin manifest at its root.");
					if (manifests.Count > 1)
						throw new InvalidDataException("The package has several .addin manifests at its root; exactly one is required.");
					// The size in the zip header is the package's own claim; read at most the cap from the real stream.
					byte[] content;
					using (Stream input = manifests[0].Open())
						content = ReadAtMost(input, MaxManifestBytes);
					using (var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, true))
						return reader.ReadToEnd();
				}
			} catch (InvalidDataException) {
				throw;
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) {
				throw new InvalidDataException("The package could not be read: " + ex.Message, ex);
			}
		}

		/// <summary>
		/// Unpacks into <paramref name="targetDirectory"/> (which must be empty or not exist). Refuses an
		/// entry that would land outside it (rooted, drive-qualified, <c>..</c>), a symbolic link, a duplicate
		/// name, more than <see cref="MaxEntries"/> entries or <see cref="MaxUncompressedBytes"/> of content.
		/// </summary>
		/// <exception cref="InvalidDataException">The package is not safe to unpack; the directory may be partly filled and should be deleted.</exception>
		public static void Extract(string packageFile, string targetDirectory)
		{
			string root = Path.GetFullPath(targetDirectory);
			if (!root.EndsWith(Path.DirectorySeparatorChar))
				root += Path.DirectorySeparatorChar;
			try {
				using (ZipArchive zip = ZipFile.OpenRead(packageFile)) {
					if (zip.Entries.Count > MaxEntries)
						throw new InvalidDataException("The package has more than " + MaxEntries + " entries.");
					Directory.CreateDirectory(root);
					long total = 0;
					foreach (ZipArchiveEntry entry in zip.Entries) {
						string relative = entry.FullName.Replace('\\', '/');
						if (relative.IndexOf('\0') >= 0 || relative.StartsWith("/", StringComparison.Ordinal)
							|| (relative.Length >= 2 && relative[1] == ':')
							|| relative.Split('/').Any(segment => segment == ".."))
							throw new InvalidDataException("The package entry '" + entry.FullName + "' is not a relative path inside the package.");
						string destination = Path.GetFullPath(Path.Combine(root, relative));
						if (!destination.StartsWith(root, StringComparison.Ordinal))
							throw new InvalidDataException("The package entry '" + entry.FullName + "' would be unpacked outside the add-in's folder.");
						if (IsSymbolicLink(entry))
							throw new InvalidDataException("The package entry '" + entry.FullName + "' is a symbolic link.");
						if (relative.EndsWith("/", StringComparison.Ordinal)) {
							Directory.CreateDirectory(destination);
							continue;
						}
						total += entry.Length;
						if (total > MaxUncompressedBytes)
							throw new InvalidDataException("The package unpacks to more than " + (MaxUncompressedBytes >> 20) + " MiB.");
						Directory.CreateDirectory(Path.GetDirectoryName(destination));
						// CreateNew: a second entry of the same name (or differing only in case) fails instead of overwriting.
						using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
						using (Stream input = entry.Open())
							CopyAtMost(input, output, entry.Length);
					}
				}
			} catch (InvalidDataException) {
				throw;
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) {
				throw new InvalidDataException("The package could not be unpacked: " + ex.Message, ex);
			}
		}

		/// <summary>Reads the whole stream, but never more than <paramref name="maxBytes"/>.</summary>
		/// <exception cref="InvalidDataException">The stream holds more than <paramref name="maxBytes"/>.</exception>
		public static byte[] ReadAtMost(Stream input, int maxBytes)
		{
			using (var content = new MemoryStream())
			{
				var buffer = new byte[16384];
				int read;
				while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (content.Length + read > maxBytes)
						throw new InvalidDataException("The package's manifest is larger than " + (maxBytes >> 10) + " KiB.");
					content.Write(buffer, 0, read);
				}
				return content.ToArray();
			}
		}

		/// <summary>Copies the stream, but refuses one that holds more than <paramref name="declaredLength"/> bytes.</summary>
		/// <exception cref="InvalidDataException">The stream is larger than declared.</exception>
		public static void CopyAtMost(Stream input, Stream output, long declaredLength)
		{
			var buffer = new byte[81920];
			long copied = 0;
			int read;
			while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
				copied += read;
				// The declared size is what the limit above counted; a lying header must not unpack more.
				if (copied > declaredLength)
					throw new InvalidDataException("A package entry is larger than its declared size.");
				output.Write(buffer, 0, read);
			}
		}

		static bool IsSymbolicLink(ZipArchiveEntry entry)
		{
			// Unix zips keep st_mode in the high 16 bits of the external attributes: 0xA000 is S_IFLNK.
			return ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
		}

		/// <summary>
		/// Rewrites the manifest's source attributes. The package cannot be allowed to claim a source: any
		/// <see cref="RepositoryManifestAttribute"/>/<see cref="ReleaseManifestAttribute"/> it carries is removed,
		/// and the real ones are written only when <paramref name="release"/> says where it was downloaded from.
		/// </summary>
		public static void PatchSourceAttributes(string manifestFile, AddInRelease release)
		{
			var document = new XmlDocument();
			var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
			using (XmlReader reader = XmlReader.Create(manifestFile, settings))
				document.Load(reader);
			XmlElement root = document.DocumentElement ?? throw new InvalidDataException("The manifest has no root element.");
			root.RemoveAttribute(RepositoryManifestAttribute);
			root.RemoveAttribute(ReleaseManifestAttribute);
			if (release != null) {
				root.SetAttribute(RepositoryManifestAttribute, release.Repository.FullName);
				root.SetAttribute(ReleaseManifestAttribute, release.Tag);
			}
			document.Save(manifestFile);
		}
	}
}
