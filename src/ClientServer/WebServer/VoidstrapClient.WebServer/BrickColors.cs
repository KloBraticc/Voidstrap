using System.Collections.Generic;
using System.Linq;

namespace VoidstrapClient.WebServer;

internal class BrickColors
{
	public static IReadOnlyCollection<int> ValidBrickColors { get; } = new int[64]
	{
		45, 1024, 11, 102, 23, 1010, 1012, 1011, 1027, 1018,
		151, 1022, 135, 1019, 1013, 107, 1028, 29, 119, 37,
		1021, 1020, 28, 141, 1029, 226, 1008, 24, 1017, 1009,
		1005, 105, 1025, 125, 101, 1007, 1016, 1032, 1004, 21,
		9, 1026, 1006, 153, 1023, 1015, 1031, 104, 5, 1030,
		18, 106, 38, 1014, 217, 192, 1001, 1, 208, 1002,
		194, 199, 26, 1003
	};

	public static bool IsValid(int id)
	{
		return ValidBrickColors.Contains(id);
	}
}
