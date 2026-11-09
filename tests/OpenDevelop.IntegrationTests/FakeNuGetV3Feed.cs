using System.Net;
using System.Text;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// A tiny in-process NuGet v3 feed: a service index, a search endpoint and per-package registration pages, so the
/// IDE's real NuGet.Protocol client can browse it and read deprecation and vulnerability data (which a local folder
/// feed cannot carry). It serves exactly what the Available tab and the details pane ask for, nothing else.
/// </summary>
public sealed class FakeNuGetV3Feed : IDisposable
{
	readonly HttpListener listener = new HttpListener();
	readonly CancellationTokenSource stop = new CancellationTokenSource();
	readonly Task serving;
	readonly Dictionary<string, (string Version, string CatalogExtra)> packages = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

	public FakeNuGetV3Feed()
	{
		var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		BaseUrl = $"http://localhost:{port}/";
		listener.Prefixes.Add(BaseUrl);
		listener.Start();
		serving = Task.Run(Serve);
	}

	public string BaseUrl { get; }

	public string IndexUrl => BaseUrl + "index.json";

	/// <summary>Adds one package version; <paramref name="catalogExtra"/> is extra JSON for its catalog entry (",\"deprecation\":{...}").</summary>
	public void AddPackage(string id, string version, string catalogExtra = "")
	{
		lock (packages) packages[id] = (version, catalogExtra);
	}

	public void Dispose()
	{
		stop.Cancel();
		listener.Close();
		try { serving.Wait(2000); } catch (AggregateException) { }
	}

	string ServiceIndex => $$"""
		{"version":"3.0.0","resources":[
		  {"@id":"{{BaseUrl}}search","@type":"SearchQueryService/3.4.0"},
		  {"@id":"{{BaseUrl}}reg/","@type":"RegistrationsBaseUrl/3.6.0"},
		  {"@id":"{{BaseUrl}}reg/","@type":"RegistrationsBaseUrl"}
		]}
		""";

	string Search(string term)
	{
		List<KeyValuePair<string, (string Version, string CatalogExtra)>> matches;
		lock (packages)
			matches = packages.Where(p => term.Length == 0 || p.Key.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
		var data = string.Join(",", matches.Select(p => $$"""
			{"@id":"{{BaseUrl}}reg/{{p.Key.ToLowerInvariant()}}/index.json","@type":"Package","registration":"{{BaseUrl}}reg/{{p.Key.ToLowerInvariant()}}/index.json",
			 "id":"{{p.Key}}","version":"{{p.Value.Version}}","description":"{{p.Key}} for tests","summary":"{{p.Key}} summary","title":"{{p.Key}}","tags":[],
			 "authors":["tests"],"totalDownloads":1,"verified":false,"packageTypes":[{"name":"Dependency"}],
			 "versions":[{"version":"{{p.Value.Version}}","downloads":1,"@id":"{{BaseUrl}}reg/{{p.Key.ToLowerInvariant()}}/{{p.Value.Version}}.json"}]}
			"""));
		return $$"""{"totalHits":{{matches.Count}},"data":[{{data}}]}""";
	}

	string? Registration(string id)
	{
		(string Version, string CatalogExtra) package;
		lock (packages)
			if (!packages.TryGetValue(id, out package))
				return null;
		var lower = id.ToLowerInvariant();
		return $$"""
			{"@id":"{{BaseUrl}}reg/{{lower}}/index.json","count":1,"items":[
			  {"@id":"{{BaseUrl}}reg/{{lower}}/page/{{package.Version}}/{{package.Version}}.json","count":1,"lower":"{{package.Version}}","upper":"{{package.Version}}","items":[
			    {"@id":"{{BaseUrl}}reg/{{lower}}/{{package.Version}}.json","catalogEntry":{
			       "@id":"{{BaseUrl}}catalog/{{lower}}.{{package.Version}}.json","id":"{{id}}","version":"{{package.Version}}",
			       "authors":"tests","description":"{{id}} for tests","listed":true,"packageContent":"{{BaseUrl}}pkg/{{lower}}.{{package.Version}}.nupkg"{{package.CatalogExtra}}},
			     "packageContent":"{{BaseUrl}}pkg/{{lower}}.{{package.Version}}.nupkg"}
			  ]}
			]}
			""";
	}

	void Serve()
	{
		while (!stop.IsCancellationRequested) {
			HttpListenerContext context;
			try { context = listener.GetContext(); } catch { return; }
			var path = context.Request.Url!.AbsolutePath;
			string? body = null;
			if (path == "/index.json")
				body = ServiceIndex;
			else if (path == "/search")
				body = Search(System.Web.HttpUtility.ParseQueryString(context.Request.Url.Query)["q"] ?? "");
			else if (path.StartsWith("/reg/", StringComparison.Ordinal) && path.EndsWith("/index.json", StringComparison.Ordinal))
				body = Registration(path.Split('/')[2]);
			var bytes = Encoding.UTF8.GetBytes(body ?? "");
			context.Response.StatusCode = body == null ? 404 : 200;
			context.Response.ContentType = "application/json";
			context.Response.OutputStream.Write(bytes, 0, bytes.Length);
			context.Response.Close();
		}
	}
}
