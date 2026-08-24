using LeXtudio.MewUI.Xaml;

// MXAML -> strict InitializeComponent C# generator (build-time companion to the designer).
// Usage: mewuigen <input.mxaml> <output.g.cs> [<input2.mxaml> <output2.g.cs> ...]
// MSBuild passes ALL inputs followed by ALL outputs (two batches of the same item list),
// so the arguments are: [in1..inN, out1..outN]. Pair them by index.
if (args.Length == 0 || args.Length % 2 != 0) {
    Console.Error.WriteLine("usage: mewuigen <in1.mxaml> ... <out1.g.cs> ...");
    return 1;
}
var half = args.Length / 2;
var inputs = args[..half];
var outputs = args[half..];
for (var i = 0; i < inputs.Length; i++) {
    var doc = MxamlDocument.Parse(File.ReadAllText(inputs[i]));
    if (doc.HasErrors) {
        foreach (var d in doc.Diagnostics.Where(d => d.Severity == MxamlDiagnosticSeverity.Error))
            Console.Error.WriteLine($"{inputs[i]}({d.Line},{d.Column}): {d.Message}");
        return 1;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputs[i]))!);
    File.WriteAllText(outputs[i], MewUICSharpGenerator.Generate(doc));
    Console.WriteLine($"MewUIGen: {inputs[i]} -> {outputs[i]}");
}
return 0;
