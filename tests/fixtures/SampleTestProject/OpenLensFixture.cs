// OpenLens layout + reference-count fixture. Declaration lines: 15, 17, 20, 22, 25,
// 27, 30, 32 (line gaps 2, 3, 2, 3, 2, 3, 2). The OpenLens integration test asserts
// (a) the lens rows keep that exact spacing pattern (a lens on the wrong line breaks
// it) and (b) the per-row reference counts match the real call graph in Uses.Total:
//   Alpha      -> 2 references (new Alpha() twice)
//   Alpha.One  -> 2 references (called twice)
//   Beta       -> 1 reference  (new Beta())
//   Beta.Two   -> 1 reference
//   Gamma      -> 1 reference  (new Gamma())
//   Gamma.Three-> 1 reference
//   Uses       -> 0 references
//   Uses.Total -> 0 references
namespace OpenLensFixture;

public class Alpha
{
    public int One() { return 1; }
}

public class Beta
{
    public int Two() { return 1; }
}

public class Gamma
{
    public int Three() { return 1; }
}

public class Uses
{
    public int Total()
    {
        return new Alpha().One() + new Alpha().One()
            + new Beta().Two() + new Gamma().Three();
    }
}