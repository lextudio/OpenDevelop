# Workflow Designer moved to CoreWF

The OpenDevelop Workflow Designer addin, its out-of-process host, tests, and design note are
maintained in the CoreWF repository:

`/Users/lextm/uno-tools/CoreWF/docs/opendevelop/workflow-designer.md`

OpenDevelop remains responsible for the common designer shell, Addin SDK, and the installed app
used by the external integration tests. On macOS those tests start
`/Applications/OpenDevelop.app` directly (or `OPENDEVELOP_APP_PATH` when overridden).
