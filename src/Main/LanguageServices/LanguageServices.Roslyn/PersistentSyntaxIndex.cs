#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn
{
    /// <summary>Content-addressed identifier index. It filters candidates, never answers semantics.</summary>
    public sealed class PersistentSyntaxIndex
    {
        readonly string directory;
        readonly ConcurrentDictionary<string, string[]> memory = new(StringComparer.Ordinal);
        long diskHits, builds;
        public long DiskHits => Interlocked.Read(ref diskHits);
        public long Builds => Interlocked.Read(ref builds);
        public PersistentSyntaxIndex(string directory) => this.directory = directory;
        public sealed record Entry(string Key, string[] Names, string Digest);
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        public async Task<bool> MayContainAsync(Document document, string identifier, CancellationToken token)
        {
            var text = await document.GetTextAsync(token).ConfigureAwait(false);
            var options = document.Project.ParseOptions;
            var languageVersion = options switch {
                Microsoft.CodeAnalysis.CSharp.CSharpParseOptions cs => cs.LanguageVersion.ToString(),
                Microsoft.CodeAnalysis.VisualBasic.VisualBasicParseOptions vb => vb.LanguageVersion.ToString(),
                _ => "unknown"
            };
            // Parsing changes with TFM symbols and compiler versions even when source bytes do
            // not change. No timestamps participate in either lookup or validation.
            var key = Hash(JsonSerializer.Serialize(new {
                schema = 1,
                compiler = typeof(Workspace).Assembly.GetName().Version?.ToString(),
                language = document.Project.Language,
                languageVersion,
                kind = options?.Kind.ToString(),
                documentation = options?.DocumentationMode.ToString(),
                symbols = options?.PreprocessorSymbolNames.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                features = options?.Features.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
                source = Hash(text.ToString())
            }));
            if (!memory.TryGetValue(key, out var names))
            {
                var path = Path.Combine(directory, key + ".json");
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024)
                    {
                        var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                        if (entry?.Key == key && entry.Names != null && entry.Names.All(n => n != null)
                            && entry.Digest == Hash(key + "\0" + string.Join("\0", entry.Names)))
                        {
                            names = entry.Names;
                            Interlocked.Increment(ref diskHits);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
                if (names == null)
                {
                    var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
                    if (root == null) return true;
                    int identifierKind = document.Project.Language == LanguageNames.VisualBasic
                        ? (int)Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.IdentifierToken
                        : (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken;
                    names = root.DescendantTokens(descendIntoTrivia: true)
                        .Where(t => t.RawKind == identifierKind).Select(t => t.ValueText)
                        .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    Interlocked.Increment(ref builds);
                    var temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        Directory.CreateDirectory(directory);
                        var entry = new Entry(key, names, Hash(key + "\0" + string.Join("\0", names)));
                        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry), token).ConfigureAwait(false);
                        // Concurrent hosts write identical immutable entries under the same key.
                        // A unique temporary file and atomic replacement prevent partial reads.
                        File.Move(temporary, path, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    finally
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                }
                if (memory.Count >= 512) memory.Clear();
                memory[key] = names;
            }
            return names.Contains(identifier, StringComparer.OrdinalIgnoreCase);
        }
    }
}
