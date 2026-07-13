using System;
using System.Collections.Generic;
using System.Text;
using Hornung.ResourceToolkit.Resolver;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;

namespace Hornung.ResourceToolkit
{
    public static class ResourceResolverService
    {
        public const string ResourceResolversAddInTreePath = "/AddIns/ResourceToolkit/Resolvers";

        static List<IResourceResolver> resolvers;

        public static IEnumerable<IResourceResolver> Resolvers {
            get {
                if (resolvers == null) {
                    resolvers = AddInTree.BuildItems<IResourceResolver>(ResourceResolversAddInTreePath, null, false);
                }
                return resolvers;
            }
        }

        public static void SetResourceResolversListUnitTestOnly(IEnumerable<IResourceResolver> resolversToSet)
        {
            resolvers = new List<IResourceResolver>(resolversToSet);
        }

        public static ResourceResolveResult Resolve(ITextEditor editor, char? charTyped)
        {
            ResourceResolveResult result;
            foreach (IResourceResolver resolver in Resolvers) {
                if ((result = resolver.Resolve(editor, charTyped)) != null) {
                    return result;
                }
            }
            return null;
        }

        public static string FormatResourceDescription(IResourceFileContent content, string key)
        {
            StringBuilder sb = new StringBuilder();

            IMultiResourceFileContent mrfc;
            if (key != null && (mrfc = (content as IMultiResourceFileContent)) != null) {
                string file = mrfc.GetFileNameForKey(key);
                if (file == null) {
                    file = content.FileName;
                }
                sb.AppendFormat(StringParser.Parse("${res:Hornung.ResourceToolkit.ToolTips.PlaceMessage}"), file);
            } else {
                sb.AppendFormat(StringParser.Parse("${res:Hornung.ResourceToolkit.ToolTips.PlaceMessage}"), content.FileName);
            }

            sb.AppendLine();
            sb.Append(StringParser.Parse("${res:Hornung.ResourceToolkit.KeyLabel}"));
            sb.Append(' ');

            if (key != null) {
                sb.AppendLine(key);
                sb.AppendLine();
                sb.AppendLine(StringParser.Parse("${res:Hornung.ResourceToolkit.ValueLabel}"));

                object value;
                if (content.TryGetValue(key, out value)) {
                    if (value is string) {
                        sb.Append(value);
                    } else {
                        sb.AppendFormat(StringParser.Parse("${res:Hornung.ResourceToolkit.ToolTips.TypeMessage}"), value.GetType().ToString());
                        sb.Append(' ');
                        sb.Append(value.ToString());
                    }
                } else {
                    sb.Append(StringParser.Parse("${res:Hornung.ResourceToolkit.ToolTips.KeyNotFound}"));
                }
            } else {
                sb.Append(StringParser.Parse("${res:Hornung.ResourceToolkit.ToolTips.UnknownKey}"));
            }

            return sb.ToString();
        }
    }
}
