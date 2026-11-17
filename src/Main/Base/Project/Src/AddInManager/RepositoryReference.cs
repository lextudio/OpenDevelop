using System;
using System.Text.RegularExpressions;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>
	/// A GitHub repository a user trusts as a source of add-ins: <c>owner/repo</c>.
	/// Names are case-insensitive, as on GitHub; the spelling the user typed is kept for display.
	/// </summary>
	public sealed class RepositoryReference : IEquatable<RepositoryReference>
	{
		static readonly Regex OwnerPattern = new Regex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.CultureInvariant);
		static readonly Regex NamePattern = new Regex(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

		RepositoryReference(string owner, string name)
		{
			Owner = owner;
			Name = name;
		}

		public string Owner { get; }
		public string Name { get; }

		/// <summary>The <c>owner/repo</c> form, as entered.</summary>
		public string FullName
		{
			get { return Owner + "/" + Name; }
		}

		/// <summary>
		/// Parses <c>owner/repo</c>, or a pasted <c>https://github.com/owner/repo</c> (optionally with a
		/// trailing slash or <c>.git</c>). Nothing else is accepted: the add-in source is always GitHub.
		/// </summary>
		public static bool TryParse(string text, out RepositoryReference repository, out string error)
		{
			repository = null;
			error = null;
			string value = (text ?? string.Empty).Trim();
			const string urlPrefix = "https://github.com/";
			if (value.StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase)) {
				value = value.Substring(urlPrefix.Length).TrimEnd('/');
				if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
					value = value.Substring(0, value.Length - 4);
			}
			string[] parts = value.Split('/');
			if (parts.Length != 2) {
				error = "Enter a repository as owner/repo, for example owner/my-addin.";
				return false;
			}
			if (!OwnerPattern.IsMatch(parts[0])) {
				error = "'" + parts[0] + "' is not a valid GitHub user or organization name.";
				return false;
			}
			if (!NamePattern.IsMatch(parts[1]) || parts[1] == "." || parts[1] == "..") {
				error = "'" + parts[1] + "' is not a valid GitHub repository name.";
				return false;
			}
			repository = new RepositoryReference(parts[0], parts[1]);
			return true;
		}

		public static RepositoryReference Parse(string text)
		{
			if (!TryParse(text, out RepositoryReference repository, out string error))
				throw new FormatException(error);
			return repository;
		}

		public bool Equals(RepositoryReference other)
		{
			return other != null
				&& string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
		}

		public override bool Equals(object obj)
		{
			return Equals(obj as RepositoryReference);
		}

		public override int GetHashCode()
		{
			return StringComparer.OrdinalIgnoreCase.GetHashCode(Owner) * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
		}

		public override string ToString()
		{
			return FullName;
		}
	}
}
