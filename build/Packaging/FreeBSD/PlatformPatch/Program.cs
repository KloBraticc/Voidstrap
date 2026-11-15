using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length < 2)
{
	Console.Error.WriteLine("Usage: PlatformPatch <output directory> <assembly>...");
	return 1;
}

string output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
string[] inputs = args.Length == 2 && args[1].EndsWith(".rsp", StringComparison.Ordinal)
	? File.ReadAllLines(args[1]).Where(line => line.Length > 0).ToArray()
	: args.Skip(1).ToArray();
string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;
string[] assemblyDirectories = inputs
	.Select(Path.GetFullPath)
	.Select(Path.GetDirectoryName)
	.OfType<string>()
	.Distinct(StringComparer.Ordinal)
	.ToArray();

foreach (string argument in inputs)
{
	string input = Path.GetFullPath(argument);
	string target = Path.Combine(output, Path.GetFileName(input));
	if (Path.GetFileName(input) is not ("PresentationCore.dll" or "ProGPU.Backend.dll" or "ProGPU.Text.dll" or "ProGPU.Wpf.dll" or "Silk.NET.Core.dll" or "Wpf.Ui.dll"))
	{
		if (!string.Equals(input, target, StringComparison.Ordinal))
			File.Copy(input, target, true);
		continue;
	}
	using DefaultAssemblyResolver resolver = new();
	string inputDirectory = Path.GetDirectoryName(input) ?? string.Empty;
	resolver.AddSearchDirectory(inputDirectory);
	foreach (string directory in assemblyDirectories)
	{
		if (!string.Equals(directory, inputDirectory, StringComparison.Ordinal) && !string.Equals(directory, runtimeDirectory, StringComparison.Ordinal))
			resolver.AddSearchDirectory(directory);
	}
	if (runtimeDirectory.Length > 0)
		resolver.AddSearchDirectory(runtimeDirectory);

	ModuleDefinition module;
	try
	{
		module = ModuleDefinition.ReadModule(input, new ReaderParameters
		{
			AssemblyResolver = resolver,
			ReadingMode = ReadingMode.Immediate,
			InMemory = true
		});
	}
	catch (BadImageFormatException)
	{
		if (!string.Equals(input, target, StringComparison.Ordinal))
			File.Copy(input, target, true);
		continue;
	}

	using ModuleDefinition loaded = module;

	int patched = 0;
	foreach (TypeDefinition type in module.GetTypes())
	{
		foreach (MethodDefinition method in type.Methods)
		{
			if (method.HasBody)
				patched += PatchLinuxChecks(method.Body);
		}
	}

	if (patched == 0)
	{
		if (!string.Equals(input, target, StringComparison.Ordinal))
			File.Copy(input, target, true);
		Console.WriteLine($"{Path.GetFileName(input)}: no Linux checks to patch");
		continue;
	}

	module.Write(target);
	Console.WriteLine($"{Path.GetFileName(input)}: patched {patched} Linux checks");
}

return 0;

static bool IsOperatingSystemCall(Instruction? instruction, string name)
{
	return instruction is not null
		&& instruction.OpCode == OpCodes.Call
		&& instruction.Operand is MethodReference method
		&& method.Name == name
		&& method.Parameters.Count == 0
		&& method.DeclaringType.FullName == "System.OperatingSystem";
}

static int PatchLinuxChecks(MethodBody body)
{
	List<Instruction> linuxCalls = body.Instructions
		.Where(instruction => IsOperatingSystemCall(instruction, "IsLinux"))
		.ToList();
	if (body.Method.DeclaringType.FullName == "ProGPU.Backend.NativeWindowPlatformFactory" && body.Method.Name == "Create")
	{
		if (linuxCalls.Count != 2 || !ReferencesNativeWindow(linuxCalls[0], linuxCalls[1], "get_X11") || !ReferencesNativeWindow(linuxCalls[1], null, "get_Wayland"))
			throw new InvalidOperationException("The native window platform checks have changed");
		linuxCalls.RemoveAt(1);
	}
	List<Instruction> calls = linuxCalls
		.Where(instruction => !IsOperatingSystemCall(instruction.Next, "IsFreeBSD"))
		.ToList();
	if (calls.Count == 0)
		return 0;

	body.SimplifyMacros();
	ILProcessor processor = body.GetILProcessor();
	foreach (Instruction call in calls)
	{
		MethodReference isLinux = (MethodReference)call.Operand;
		MethodReference isFreeBSD = new("IsFreeBSD", isLinux.ReturnType, isLinux.DeclaringType);
		processor.InsertAfter(call, processor.Create(OpCodes.Or));
		processor.InsertAfter(call, processor.Create(OpCodes.Call, isFreeBSD));
	}
	body.OptimizeMacros();
	return calls.Count;
}

static bool ReferencesNativeWindow(Instruction start, Instruction? end, string name)
{
	for (Instruction? instruction = start.Next; instruction is not null && instruction != end; instruction = instruction.Next)
	{
		if (instruction.Operand is MethodReference method && method.DeclaringType.FullName == "Silk.NET.Core.Contexts.INativeWindow" && method.Name == name)
			return true;
	}
	return false;
}
