using System;
using ICSharpCode.Core;

namespace Hornung.ResourceToolkit.Resolver
{
    public static class NRefactoryAstCacheService
    {
        public static bool CacheEnabled {
            get { return RoslynAstCacheService.CacheEnabled; }
        }

        public static event EventHandler CacheEnabledChanged {
            add { RoslynAstCacheService.CacheEnabledChanged += value; }
            remove { RoslynAstCacheService.CacheEnabledChanged -= value; }
        }

        public static void EnableCache()
        {
            RoslynAstCacheService.EnableCache();
        }

        public static void DisableCache()
        {
            RoslynAstCacheService.DisableCache();
        }
    }
}
