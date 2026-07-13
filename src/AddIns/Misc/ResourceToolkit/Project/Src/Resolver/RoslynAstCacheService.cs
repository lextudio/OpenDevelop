using System;
using System.Collections.Generic;
using ICSharpCode.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Hornung.ResourceToolkit.Resolver
{
    public static class RoslynAstCacheService
    {
        static bool cacheEnabled;
        static Dictionary<string, SyntaxTree> cachedSyntaxTrees = new Dictionary<string, SyntaxTree>();

        public static bool CacheEnabled {
            get { return cacheEnabled; }
        }

        public static event EventHandler CacheEnabledChanged;

        static void OnCacheEnabledChanged(EventArgs e)
        {
            if (CacheEnabledChanged != null) {
                CacheEnabledChanged(null, e);
            }
        }

        public static void EnableCache()
        {
            if (CacheEnabled) {
                throw new InvalidOperationException("The AST cache is already enabled.");
            }
            cacheEnabled = true;
            LoggingService.Info("ResourceToolkit: RoslynAstCacheService cache enabled");
            OnCacheEnabledChanged(EventArgs.Empty);
        }

        public static void DisableCache()
        {
            if (CacheEnabled) {
                cacheEnabled = false;
                cachedSyntaxTrees.Clear();
                LoggingService.Info("ResourceToolkit: RoslynAstCacheService cache disabled and cleared");
                OnCacheEnabledChanged(EventArgs.Empty);
            }
        }

        public static SyntaxTree GetSyntaxTree(string fileName, string fileContent)
        {
            SyntaxTree tree;
            if (!CacheEnabled || !cachedSyntaxTrees.TryGetValue(fileName, out tree)) {
                tree = CSharpSyntaxTree.ParseText(fileContent, new CSharpParseOptions(LanguageVersion.CSharp9), fileName);
                if (tree != null && CacheEnabled) {
                    cachedSyntaxTrees.Add(fileName, tree);
                }
            }
            return tree;
        }

        public static string GenerateKeyLiteral(string key)
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(key)).ToFullString();
        }
    }
}
