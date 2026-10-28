# LeXtudio.MewUI.Xaml (.mxaml) — compatibility note

This file used to duplicate an early MXAML proposal. It described a `<MewUI>` wrapper, a nested
`<Window>`, permissive unknown properties, and an unfinished Roslyn-to-MXAML cutover. None is the
current format or host behavior.

The canonical, maintained MXAML specification is [`mxaml.md`](mxaml.md). In particular, a design
document's root is `<Window Class="…" Name="…">`, the MewUI host edits that document directly,
and the runtime never reads MXAML. Keep inbound links to this filename working, but add new design
or implementation detail only to `mxaml.md` and [`mewui-designer.md`](mewui-designer.md).
