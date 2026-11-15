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
string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;

foreach (string argument in args.Skip(1))
{
	string input = Path.GetFullPath(argument);
	string target = Path.Combine(output, Path.GetFileName(input));
	using DefaultAssemblyResolver resolver = new();
	resolver.AddSearchDirectory(Path.GetDirectoryName(input) ?? string.Empty);
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
	List<Instruction> calls = body.Instructions
		.Where(instruction => IsOperatingSystemCall(instruction, "IsLinux") && !IsOperatingSystemCall(instruction.Next, "IsFreeBSD"))
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
