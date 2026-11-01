using System.Linq;
using System.Text.Json;
using ICSharpCode.Core;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.Translations;

public sealed class RegisterDevFlowActionsCommand : DevFlowActionRegistrationCommand { }

[DevFlowUIThread]
public static class TranslationsDevFlowActions
{
	[DevFlowAction("od.translations.update", Description = "Regenerate a project's po/<project>.pot from its translatable sources and merge every po/*.po; the project is the one whose file is given, else the current project")]
	public static string Update(string projectFile = "")
	{
		var result = TranslationsService.Update(TranslationsService.FindProject(projectFile));
		return JsonSerializer.Serialize(new {
			success = result != null,
			potPath = result?.PotPath, stringCount = result?.StringCount ?? 0, potWritten = result?.PotWritten ?? false,
			updatedCatalogs = result?.UpdatedCatalogs ?? System.Array.Empty<string>()
		});
	}
}
