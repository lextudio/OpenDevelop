// Pins SharpDevelop's DevFlow agent to a port dedicated to this app, instead of the shared
// default (9223), which on this machine collides with Wino.Mail and other unrelated local
// services. OpenDevelopAppFixture (tests/OpenDevelop.IntegrationTests) must use the same port.
[assembly: System.Reflection.AssemblyMetadata("Microsoft.Maui.DevFlowPort", "9299")]
