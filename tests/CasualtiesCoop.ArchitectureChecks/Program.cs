using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length is < 2 or > 3)
{
    throw new ArgumentException("Provide the repository directory, the dotnet executable and, optionally, the built plugin path.");
}

string root = args[0];
string dotnet = args[1];
const string core = "CasualtiesCoop.Core";
const string protocol = "CasualtiesCoop.Protocol";

CheckAssembly(core, protocol);
CheckAssembly(protocol);
await CheckProject(core, protocol);
await CheckProject(protocol);
ExpectRejected(() => CheckProjectItems(core, """{"Items":{"ProjectReference":[],"Reference":[{"Identity":"UnityEngine.CoreModule"}],"PackageReference":[]}}""", protocol));
ExpectRejected(() => CheckProjectItems(protocol, """{"Items":{"ProjectReference":[{"Identity":"../CasualtiesCoop.Core/CasualtiesCoop.Core.csproj"}],"Reference":[],"PackageReference":[]}}"""));
ExpectRejected(() => RequireAssemblyReference(protocol, "com.rlabrecque.steamworks.net", null));
if (args.Length == 3)
{
    CheckReleaseBundle(args[2]);
}
Console.WriteLine("Architecture references and rejection checks passed.");

static void CheckReleaseBundle(string pluginPath)
{
    string folder = Path.GetDirectoryName(pluginPath)!;
    foreach (string testAssembly in new[] { "CasualtiesCoop.Testing.dll", "CasualtiesCoop.TestKit.dll" })
    {
        if (File.Exists(Path.Combine(folder, testAssembly)))
        {
            throw new InvalidOperationException($"Production output contains {testAssembly}.");
        }
    }

    using var plugin = File.OpenRead(pluginPath);
    using var pe = new PEReader(plugin);
    MetadataReader reader = pe.GetMetadataReader();
    foreach (AssemblyReferenceHandle reference in reader.AssemblyReferences)
    {
        string name = reader.GetString(reader.GetAssemblyReference(reference).Name);
        if (name is "CasualtiesCoop.Testing" or "CasualtiesCoop.TestKit")
        {
            throw new InvalidOperationException($"Production references {name}.");
        }
    }

    var fingerprints = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
    {
        CustomAttribute attribute = reader.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference ||
            reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is not
                { Kind: HandleKind.TypeReference } parent)
        {
            continue;
        }

        TypeReference type = reader.GetTypeReference((TypeReferenceHandle)parent);
        if (reader.GetString(type.Name) != "AssemblyMetadataAttribute" ||
            reader.GetString(type.Namespace) != "System.Reflection")
        {
            continue;
        }

        BlobReader blob = reader.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() == 1 && blob.ReadSerializedString() is { } key)
        {
            fingerprints.Add(key, blob.ReadSerializedString());
        }
    }

    string release = Path.GetDirectoryName(pluginPath)!;
    foreach (string module in new[] { core, protocol })
    {
        using var assembly = File.OpenRead(Path.Combine(release, module + ".dll"));
        string hash = Convert.ToHexString(SHA256.HashData(assembly));
        if (!fingerprints.TryGetValue(module + ".SHA256", out string? expected) || expected != hash)
        {
            throw new InvalidOperationException($"The Release plugin fingerprint does not match {module}.dll.");
        }
    }
    Console.WriteLine("Release plugin fingerprints match the dependency DLLs.");
}

static void CheckAssembly(string assembly, string? allowedProject = null)
{
    using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, assembly + ".dll"));
    using var pe = new PEReader(stream);
    MetadataReader reader = pe.GetMetadataReader();
    foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
    {
        string name = reader.GetString(reader.GetAssemblyReference(handle).Name);
        RequireAssemblyReference(assembly, name, allowedProject);
    }
}

static void RequireAssemblyReference(string assembly, string name, string? allowedProject)
{
    bool framework = name is "netstandard" or "mscorlib" or "System" || name.StartsWith("System.", StringComparison.Ordinal);
    if (!framework && name != allowedProject)
    {
        throw new InvalidOperationException($"{assembly} references forbidden assembly {name}.");
    }
}

async Task CheckProject(string project, string? allowedProject = null)
{
    var start = new ProcessStartInfo(dotnet)
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    start.ArgumentList.Add("msbuild");
    start.ArgumentList.Add(Path.Combine("src", project, project + ".csproj"));
    start.ArgumentList.Add("-p:Configuration=Release");
    start.ArgumentList.Add("-getItem:ProjectReference,Reference,PackageReference");
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MSBuild.");
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Could not evaluate {project}: {await error}\n{await output}");
    }

    CheckProjectItems(project, await output, allowedProject);
}

static void CheckProjectItems(string project, string json, string? allowedProject = null)
{
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement items = document.RootElement.GetProperty("Items");
    foreach (string type in new[] { "ProjectReference", "Reference", "PackageReference" })
    {
        foreach (JsonElement item in items.GetProperty(type).EnumerateArray())
        {
            string name = item.GetProperty("Identity").GetString()!;
            string allowedPath = $"../{allowedProject}/{allowedProject}.csproj";
            bool permitted = type == "ProjectReference" && allowedProject is not null &&
                name.Replace('\\', '/') == allowedPath;
            if (!permitted)
            {
                throw new InvalidOperationException($"{project} declares forbidden {type} {name}.");
            }
        }
    }
}

static void ExpectRejected(Action check)
{
    try
    {
        check();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException("The architecture check accepted a forbidden dependency.");
}
