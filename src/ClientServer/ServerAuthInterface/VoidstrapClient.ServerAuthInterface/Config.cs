using System;
using System.Collections.Generic;
using CommandLine;
using CommandLine.Text;

namespace VoidstrapClient.ServerAuthInterface;

internal class Config
{
	private static Config? _Default { get; set; }

	public static Config Default => _Default ?? throw new Exception("Tried to get config before finished loading!");

	[Option('p', "port", Required = true)]
	public ushort BasePort { get; set; }

	public ushort ProxyPort => BasePort;

	public ushort WebServerPort => BasePort;

	public ushort RobloxPort => (ushort)(BasePort + 1);

	[Option('c', "clientprocessid", Required = false, Default = -1)]
	public int ClientProcessId { get; set; } = -1;

	public static void Load(string[] args)
	{
		if (args.Length == 0)
		{
			args = new string[1] { "--help" };
		}
		Parser parser = new Parser(delegate(ParserSettings settings)
		{
			settings.CaseInsensitiveEnumValues = true;
		});
		ParserResult<Config> configParser = parser.ParseArguments<Config>(args);
		configParser.WithNotParsed(delegate(IEnumerable<Error> error)
		{
			Error(configParser, error);
		});
		configParser.WithParsed<Config>(delegate(Config config)
		{
			if (config.BasePort is < 1 or > 65534)
			{
				Console.Error.WriteLine("Base port must be between 1 and 65534");
				Environment.Exit(2);
				return;
			}
			_Default = config;
		});
	}

	private static void Error(ParserResult<Config> config, IEnumerable<Error> errors)
	{
		HelpText helpText = HelpText.AutoBuild(config);
		Console.WriteLine(helpText);
		Environment.Exit(2);
	}
}
