using System.Collections.Generic;
using System.Linq;

namespace Voidstrap.Models;

public static class RobloxServerLocations
{
	public static readonly IReadOnlyList<RobloxServerLocation> All = new RobloxServerLocation[105]
	{
		new RobloxServerLocation
		{
			Key = "us-ashburn",
			City = "Ashburn",
			Region = "Virginia, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "ashburn", "loudoun" },
			Lat = 39.0438,
			Lon = -77.4874
		},
		new RobloxServerLocation
		{
			Key = "us-reston",
			City = "Reston",
			Region = "Virginia, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "reston", "fairfax" },
			Lat = 38.9586,
			Lon = -77.357
		},
		new RobloxServerLocation
		{
			Key = "us-sterling",
			City = "Sterling",
			Region = "Virginia, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "sterling" },
			Lat = 39.0062,
			Lon = -77.4286
		},
		new RobloxServerLocation
		{
			Key = "us-mclean",
			City = "McLean",
			Region = "Virginia, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "mclean", "tysons" },
			Lat = 38.9341,
			Lon = -77.1773
		},
		new RobloxServerLocation
		{
			Key = "us-washington-dc",
			City = "Washington D.C.",
			Region = "DC, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "washington" },
			Lat = 38.9072,
			Lon = -77.0369
		},
		new RobloxServerLocation
		{
			Key = "us-chicago",
			City = "Chicago",
			Region = "Illinois, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "chicago" },
			Lat = 41.8781,
			Lon = -87.6298
		},
		new RobloxServerLocation
		{
			Key = "us-dallas",
			City = "Dallas",
			Region = "Texas, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[4] { "dallas", "plano", "richardson", "irving" },
			Lat = 32.7767,
			Lon = -96.797
		},
		new RobloxServerLocation
		{
			Key = "us-houston",
			City = "Houston",
			Region = "Texas, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "houston" },
			Lat = 29.7604,
			Lon = -95.3698
		},
		new RobloxServerLocation
		{
			Key = "us-san-antonio",
			City = "San Antonio",
			Region = "Texas, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "san antonio" },
			Lat = 29.4241,
			Lon = -98.4936
		},
		new RobloxServerLocation
		{
			Key = "us-atlanta",
			City = "Atlanta",
			Region = "Georgia, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "atlanta" },
			Lat = 33.749,
			Lon = -84.388
		},
		new RobloxServerLocation
		{
			Key = "us-miami",
			City = "Miami",
			Region = "Florida, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "miami" },
			Lat = 25.7617,
			Lon = -80.1918
		},
		new RobloxServerLocation
		{
			Key = "us-tampa",
			City = "Tampa",
			Region = "Florida, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "tampa" },
			Lat = 27.9506,
			Lon = -82.4572
		},
		new RobloxServerLocation
		{
			Key = "us-newark",
			City = "Newark",
			Region = "New Jersey, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[3] { "newark", "secaucus", "piscataway" },
			Lat = 40.7357,
			Lon = -74.1724
		},
		new RobloxServerLocation
		{
			Key = "us-new-york",
			City = "New York",
			Region = "New York, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[4] { "new york", "manhattan", "brooklyn", "queens" },
			Lat = 40.7128,
			Lon = -74.006
		},
		new RobloxServerLocation
		{
			Key = "us-buffalo",
			City = "Buffalo",
			Region = "New York, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "buffalo" },
			Lat = 42.8864,
			Lon = -78.8784
		},
		new RobloxServerLocation
		{
			Key = "us-detroit",
			City = "Detroit",
			Region = "Michigan, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "detroit" },
			Lat = 42.3314,
			Lon = -83.0458
		},
		new RobloxServerLocation
		{
			Key = "us-columbus",
			City = "Columbus",
			Region = "Ohio, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "columbus", "dublin oh" },
			Lat = 39.9612,
			Lon = -82.9988
		},
		new RobloxServerLocation
		{
			Key = "us-cleveland",
			City = "Cleveland",
			Region = "Ohio, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "cleveland" },
			Lat = 41.4993,
			Lon = -81.6944
		},
		new RobloxServerLocation
		{
			Key = "us-minneapolis",
			City = "Minneapolis",
			Region = "Minnesota, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "minneapolis", "st paul" },
			Lat = 44.9778,
			Lon = -93.265
		},
		new RobloxServerLocation
		{
			Key = "us-kansas-city",
			City = "Kansas City",
			Region = "Missouri, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "kansas city" },
			Lat = 39.0997,
			Lon = -94.5786
		},
		new RobloxServerLocation
		{
			Key = "us-denver",
			City = "Denver",
			Region = "Colorado, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "denver" },
			Lat = 39.7392,
			Lon = -104.9903
		},
		new RobloxServerLocation
		{
			Key = "us-phoenix",
			City = "Phoenix",
			Region = "Arizona, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "phoenix" },
			Lat = 33.4484,
			Lon = -112.074
		},
		new RobloxServerLocation
		{
			Key = "us-salt-lake-city",
			City = "Salt Lake City",
			Region = "Utah, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "salt lake city" },
			Lat = 40.7608,
			Lon = -111.891
		},
		new RobloxServerLocation
		{
			Key = "us-los-angeles",
			City = "Los Angeles",
			Region = "California, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[3] { "los angeles", "el segundo", "playa vista" },
			Lat = 34.0522,
			Lon = -118.2437
		},
		new RobloxServerLocation
		{
			Key = "us-san-jose",
			City = "San Jose",
			Region = "California, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "san jose" },
			Lat = 37.3382,
			Lon = -121.8863
		},
		new RobloxServerLocation
		{
			Key = "us-santa-clara",
			City = "Santa Clara",
			Region = "California, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "santa clara" },
			Lat = 37.3541,
			Lon = -121.9552
		},
		new RobloxServerLocation
		{
			Key = "us-palo-alto",
			City = "Palo Alto",
			Region = "California, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "palo alto" },
			Lat = 37.4419,
			Lon = -122.143
		},
		new RobloxServerLocation
		{
			Key = "us-san-mateo",
			City = "San Mateo",
			Region = "California, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "san mateo" },
			Lat = 37.563,
			Lon = -122.3255
		},
		new RobloxServerLocation
		{
			Key = "us-san-francisco",
			City = "San Francisco",
			Region = "California, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "san francisco" },
			Lat = 37.7749,
			Lon = -122.4194
		},
		new RobloxServerLocation
		{
			Key = "us-sacramento",
			City = "Sacramento",
			Region = "California, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "sacramento" },
			Lat = 38.5816,
			Lon = -121.4944
		},
		new RobloxServerLocation
		{
			Key = "us-hillsboro",
			City = "Hillsboro",
			Region = "Oregon, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "hillsboro", "boardman" },
			Lat = 45.5229,
			Lon = -122.9898
		},
		new RobloxServerLocation
		{
			Key = "us-portland",
			City = "Portland",
			Region = "Oregon, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "portland" },
			Lat = 45.5152,
			Lon = -122.6784
		},
		new RobloxServerLocation
		{
			Key = "us-seattle",
			City = "Seattle",
			Region = "Washington, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[3] { "seattle", "redmond", "bellevue" },
			Lat = 47.6062,
			Lon = -122.3321
		},
		new RobloxServerLocation
		{
			Key = "us-las-vegas",
			City = "Las Vegas",
			Region = "Nevada, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "las vegas" },
			Lat = 36.1699,
			Lon = -115.1398
		},
		new RobloxServerLocation
		{
			Key = "us-omaha",
			City = "Omaha",
			Region = "Nebraska, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "omaha" },
			Lat = 41.2565,
			Lon = -95.9345
		},
		new RobloxServerLocation
		{
			Key = "us-charlotte",
			City = "Charlotte",
			Region = "North Carolina, USA",
			Group = ServerRegion.UnitedStates,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "charlotte" },
			Lat = 35.2271,
			Lon = -80.8431
		},
		new RobloxServerLocation
		{
			Key = "ca-toronto",
			City = "Toronto",
			Region = "Ontario, Canada",
			Group = ServerRegion.Canada,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "toronto" },
			Lat = 43.6532,
			Lon = -79.3832
		},
		new RobloxServerLocation
		{
			Key = "ca-montreal",
			City = "Montreal",
			Region = "Quebec, Canada",
			Group = ServerRegion.Canada,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "montreal", "montréal" },
			Lat = 45.5017,
			Lon = -73.5673
		},
		new RobloxServerLocation
		{
			Key = "ca-vancouver",
			City = "Vancouver",
			Region = "BC, Canada",
			Group = ServerRegion.Canada,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "vancouver" },
			Lat = 49.2827,
			Lon = -123.1207
		},
		new RobloxServerLocation
		{
			Key = "ca-calgary",
			City = "Calgary",
			Region = "Alberta, Canada",
			Group = ServerRegion.Canada,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "calgary" },
			Lat = 51.0447,
			Lon = -114.0719
		},
		new RobloxServerLocation
		{
			Key = "eu-london",
			City = "London",
			Region = "United Kingdom",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "london", "slough" },
			Lat = 51.5074,
			Lon = -0.1278
		},
		new RobloxServerLocation
		{
			Key = "eu-manchester",
			City = "Manchester",
			Region = "United Kingdom",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "manchester" },
			Lat = 53.4808,
			Lon = -2.2426
		},
		new RobloxServerLocation
		{
			Key = "eu-dublin",
			City = "Dublin",
			Region = "Ireland",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "dublin" },
			Lat = 53.3498,
			Lon = -6.2603
		},
		new RobloxServerLocation
		{
			Key = "eu-amsterdam",
			City = "Amsterdam",
			Region = "Netherlands",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "amsterdam" },
			Lat = 52.3676,
			Lon = 4.9041
		},
		new RobloxServerLocation
		{
			Key = "eu-paris",
			City = "Paris",
			Region = "France",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "paris" },
			Lat = 48.8566,
			Lon = 2.3522
		},
		new RobloxServerLocation
		{
			Key = "eu-marseille",
			City = "Marseille",
			Region = "France",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "marseille" },
			Lat = 43.2965,
			Lon = 5.3698
		},
		new RobloxServerLocation
		{
			Key = "eu-frankfurt",
			City = "Frankfurt",
			Region = "Germany",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "frankfurt", "eschborn" },
			Lat = 50.1109,
			Lon = 8.6821
		},
		new RobloxServerLocation
		{
			Key = "eu-berlin",
			City = "Berlin",
			Region = "Germany",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "berlin" },
			Lat = 52.52,
			Lon = 13.405
		},
		new RobloxServerLocation
		{
			Key = "eu-munich",
			City = "Munich",
			Region = "Germany",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[3] { "munich", "münchen", "munchen" },
			Lat = 48.1351,
			Lon = 11.582
		},
		new RobloxServerLocation
		{
			Key = "eu-zurich",
			City = "Zurich",
			Region = "Switzerland",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "zurich", "zürich" },
			Lat = 47.3769,
			Lon = 8.5417
		},
		new RobloxServerLocation
		{
			Key = "eu-vienna",
			City = "Vienna",
			Region = "Austria",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "vienna", "wien" },
			Lat = 48.2082,
			Lon = 16.3738
		},
		new RobloxServerLocation
		{
			Key = "eu-warsaw",
			City = "Warsaw",
			Region = "Poland",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "warsaw", "warszawa" },
			Lat = 52.2297,
			Lon = 21.0122
		},
		new RobloxServerLocation
		{
			Key = "eu-stockholm",
			City = "Stockholm",
			Region = "Sweden",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "stockholm" },
			Lat = 59.3293,
			Lon = 18.0686
		},
		new RobloxServerLocation
		{
			Key = "eu-helsinki",
			City = "Helsinki",
			Region = "Finland",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "helsinki" },
			Lat = 60.1699,
			Lon = 24.9384
		},
		new RobloxServerLocation
		{
			Key = "eu-oslo",
			City = "Oslo",
			Region = "Norway",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "oslo" },
			Lat = 59.9139,
			Lon = 10.7522
		},
		new RobloxServerLocation
		{
			Key = "eu-copenhagen",
			City = "Copenhagen",
			Region = "Denmark",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "copenhagen", "københavn" },
			Lat = 55.6761,
			Lon = 12.5683
		},
		new RobloxServerLocation
		{
			Key = "eu-madrid",
			City = "Madrid",
			Region = "Spain",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "madrid" },
			Lat = 40.4168,
			Lon = -3.7038
		},
		new RobloxServerLocation
		{
			Key = "eu-milan",
			City = "Milan",
			Region = "Italy",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "milan", "milano" },
			Lat = 45.4642,
			Lon = 9.19
		},
		new RobloxServerLocation
		{
			Key = "eu-rome",
			City = "Rome",
			Region = "Italy",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "rome", "roma" },
			Lat = 41.9028,
			Lon = 12.4964
		},
		new RobloxServerLocation
		{
			Key = "eu-brussels",
			City = "Brussels",
			Region = "Belgium",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "brussels", "bruxelles" },
			Lat = 50.8503,
			Lon = 4.3517
		},
		new RobloxServerLocation
		{
			Key = "eu-lisbon",
			City = "Lisbon",
			Region = "Portugal",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "lisbon", "lisboa" },
			Lat = 38.7223,
			Lon = -9.1393
		},
		new RobloxServerLocation
		{
			Key = "eu-prague",
			City = "Prague",
			Region = "Czech Republic",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "prague", "praha" },
			Lat = 50.0755,
			Lon = 14.4378
		},
		new RobloxServerLocation
		{
			Key = "eu-budapest",
			City = "Budapest",
			Region = "Hungary",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "budapest" },
			Lat = 47.4979,
			Lon = 19.0402
		},
		new RobloxServerLocation
		{
			Key = "eu-bucharest",
			City = "Bucharest",
			Region = "Romania",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "bucharest", "bucurești" },
			Lat = 44.4268,
			Lon = 26.1025
		},
		new RobloxServerLocation
		{
			Key = "eu-sofia",
			City = "Sofia",
			Region = "Bulgaria",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "sofia" },
			Lat = 42.6977,
			Lon = 23.3219
		},
		new RobloxServerLocation
		{
			Key = "eu-athens",
			City = "Athens",
			Region = "Greece",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "athens", "athina" },
			Lat = 37.9838,
			Lon = 23.7275
		},
		new RobloxServerLocation
		{
			Key = "eu-istanbul",
			City = "Istanbul",
			Region = "Turkey",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "istanbul" },
			Lat = 41.0082,
			Lon = 28.9784
		},
		new RobloxServerLocation
		{
			Key = "eu-kyiv",
			City = "Kyiv",
			Region = "Ukraine",
			Group = ServerRegion.Europe,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "kyiv", "kiev" },
			Lat = 50.4501,
			Lon = 30.5234
		},
		new RobloxServerLocation
		{
			Key = "as-singapore",
			City = "Singapore",
			Region = "Singapore",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "singapore" },
			Lat = 1.3521,
			Lon = 103.8198
		},
		new RobloxServerLocation
		{
			Key = "as-tokyo",
			City = "Tokyo",
			Region = "Japan",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[3] { "tokyo", "shinjuku", "minato" },
			Lat = 35.6762,
			Lon = 139.6503
		},
		new RobloxServerLocation
		{
			Key = "as-osaka",
			City = "Osaka",
			Region = "Japan",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "osaka" },
			Lat = 34.6937,
			Lon = 135.5023
		},
		new RobloxServerLocation
		{
			Key = "as-mumbai",
			City = "Mumbai",
			Region = "India",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "mumbai", "bombay" },
			Lat = 19.076,
			Lon = 72.8777
		},
		new RobloxServerLocation
		{
			Key = "as-hyderabad",
			City = "Hyderabad",
			Region = "India",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "hyderabad" },
			Lat = 17.385,
			Lon = 78.4867
		},
		new RobloxServerLocation
		{
			Key = "as-delhi",
			City = "Delhi",
			Region = "India",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "delhi", "new delhi" },
			Lat = 28.6139,
			Lon = 77.209
		},
		new RobloxServerLocation
		{
			Key = "as-bangalore",
			City = "Bangalore",
			Region = "India",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "bangalore", "bengaluru" },
			Lat = 12.9716,
			Lon = 77.5946
		},
		new RobloxServerLocation
		{
			Key = "as-seoul",
			City = "Seoul",
			Region = "South Korea",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "seoul" },
			Lat = 37.5665,
			Lon = 126.978
		},
		new RobloxServerLocation
		{
			Key = "as-hong-kong",
			City = "Hong Kong",
			Region = "Hong Kong",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "hong kong" },
			Lat = 22.3193,
			Lon = 114.1694
		},
		new RobloxServerLocation
		{
			Key = "as-jakarta",
			City = "Jakarta",
			Region = "Indonesia",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "jakarta" },
			Lat = -6.2088,
			Lon = 106.8456
		},
		new RobloxServerLocation
		{
			Key = "as-taipei",
			City = "Taipei",
			Region = "Taiwan",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "taipei", "taiwan" },
			Lat = 25.033,
			Lon = 121.5654
		},
		new RobloxServerLocation
		{
			Key = "as-bangkok",
			City = "Bangkok",
			Region = "Thailand",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "bangkok" },
			Lat = 13.7563,
			Lon = 100.5018
		},
		new RobloxServerLocation
		{
			Key = "as-kuala-lumpur",
			City = "Kuala Lumpur",
			Region = "Malaysia",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "kuala lumpur" },
			Lat = 3.139,
			Lon = 101.6869
		},
		new RobloxServerLocation
		{
			Key = "as-manila",
			City = "Manila",
			Region = "Philippines",
			Group = ServerRegion.Asia,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "manila", "quezon" },
			Lat = 14.5995,
			Lon = 120.9842
		},
		new RobloxServerLocation
		{
			Key = "oc-sydney",
			City = "Sydney",
			Region = "Australia",
			Group = ServerRegion.Oceania,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "sydney" },
			Lat = -33.8688,
			Lon = 151.2093
		},
		new RobloxServerLocation
		{
			Key = "oc-melbourne",
			City = "Melbourne",
			Region = "Australia",
			Group = ServerRegion.Oceania,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[1] { "melbourne" },
			Lat = -37.8136,
			Lon = 144.9631
		},
		new RobloxServerLocation
		{
			Key = "oc-brisbane",
			City = "Brisbane",
			Region = "Australia",
			Group = ServerRegion.Oceania,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "brisbane" },
			Lat = -27.4698,
			Lon = 153.0251
		},
		new RobloxServerLocation
		{
			Key = "oc-perth",
			City = "Perth",
			Region = "Australia",
			Group = ServerRegion.Oceania,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "perth" },
			Lat = -31.9505,
			Lon = 115.8605
		},
		new RobloxServerLocation
		{
			Key = "oc-auckland",
			City = "Auckland",
			Region = "New Zealand",
			Group = ServerRegion.Oceania,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "auckland" },
			Lat = -36.8485,
			Lon = 174.7633
		},
		new RobloxServerLocation
		{
			Key = "sa-sao-paulo",
			City = "São Paulo",
			Region = "Brazil",
			Group = ServerRegion.SouthAmerica,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[3] { "são paulo", "sao paulo", "saopaulo" },
			Lat = -23.5505,
			Lon = -46.6333
		},
		new RobloxServerLocation
		{
			Key = "sa-rio",
			City = "Rio de Janeiro",
			Region = "Brazil",
			Group = ServerRegion.SouthAmerica,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "rio de janeiro", "rio" },
			Lat = -22.9068,
			Lon = -43.1729
		},
		new RobloxServerLocation
		{
			Key = "sa-buenos-aires",
			City = "Buenos Aires",
			Region = "Argentina",
			Group = ServerRegion.SouthAmerica,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "buenos aires" },
			Lat = -34.6037,
			Lon = -58.3816
		},
		new RobloxServerLocation
		{
			Key = "sa-santiago",
			City = "Santiago",
			Region = "Chile",
			Group = ServerRegion.SouthAmerica,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "santiago" },
			Lat = -33.4489,
			Lon = -70.6693
		},
		new RobloxServerLocation
		{
			Key = "sa-bogota",
			City = "Bogotá",
			Region = "Colombia",
			Group = ServerRegion.SouthAmerica,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "bogota", "bogotá" },
			Lat = 4.711,
			Lon = -74.0721
		},
		new RobloxServerLocation
		{
			Key = "sa-lima",
			City = "Lima",
			Region = "Peru",
			Group = ServerRegion.SouthAmerica,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "lima" },
			Lat = -12.0464,
			Lon = -77.0428
		},
		new RobloxServerLocation
		{
			Key = "sa-mexico-city",
			City = "Mexico City",
			Region = "Mexico",
			Group = ServerRegion.SouthAmerica,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[3] { "mexico city", "ciudad de méxico", "ciudad de mexico" },
			Lat = 19.4326,
			Lon = -99.1332
		},
		new RobloxServerLocation
		{
			Key = "sa-queretaro",
			City = "Querétaro",
			Region = "Mexico",
			Group = ServerRegion.SouthAmerica,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "querétaro", "queretaro" },
			Lat = 20.5888,
			Lon = -100.3899
		},
		new RobloxServerLocation
		{
			Key = "me-bahrain",
			City = "Bahrain",
			Region = "Bahrain",
			Group = ServerRegion.MiddleEast,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "bahrain", "manama" },
			Lat = 26.0667,
			Lon = 50.5577
		},
		new RobloxServerLocation
		{
			Key = "me-dubai",
			City = "Dubai",
			Region = "UAE",
			Group = ServerRegion.MiddleEast,
			Status = ServerStatusTier.Current,
			MatchTokens = new string[2] { "dubai", "abu dhabi" },
			Lat = 25.2048,
			Lon = 55.2708
		},
		new RobloxServerLocation
		{
			Key = "me-tel-aviv",
			City = "Tel Aviv",
			Region = "Israel",
			Group = ServerRegion.MiddleEast,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "tel aviv" },
			Lat = 32.0853,
			Lon = 34.7818
		},
		new RobloxServerLocation
		{
			Key = "me-riyadh",
			City = "Riyadh",
			Region = "Saudi Arabia",
			Group = ServerRegion.MiddleEast,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "riyadh" },
			Lat = 24.7136,
			Lon = 46.6753
		},
		new RobloxServerLocation
		{
			Key = "me-doha",
			City = "Doha",
			Region = "Qatar",
			Group = ServerRegion.MiddleEast,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "doha" },
			Lat = 25.2854,
			Lon = 51.531
		},
		new RobloxServerLocation
		{
			Key = "af-cape-town",
			City = "Cape Town",
			Region = "South Africa",
			Group = ServerRegion.Africa,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[1] { "cape town" },
			Lat = -33.9249,
			Lon = 18.4241
		},
		new RobloxServerLocation
		{
			Key = "af-johannesburg",
			City = "Johannesburg",
			Region = "South Africa",
			Group = ServerRegion.Africa,
			Status = ServerStatusTier.Rare,
			MatchTokens = new string[2] { "johannesburg", "jhb" },
			Lat = -26.2041,
			Lon = 28.0473
		},
		new RobloxServerLocation
		{
			Key = "af-lagos",
			City = "Lagos",
			Region = "Nigeria",
			Group = ServerRegion.Africa,
			Status = ServerStatusTier.Experimental,
			MatchTokens = new string[1] { "lagos" },
			Lat = 6.5244,
			Lon = 3.3792
		},
		new RobloxServerLocation
		{
			Key = "af-nairobi",
			City = "Nairobi",
			Region = "Kenya",
			Group = ServerRegion.Africa,
			Status = ServerStatusTier.Experimental,
			MatchTokens = new string[1] { "nairobi" },
			Lat = -1.2921,
			Lon = 36.8219
		},
		new RobloxServerLocation
		{
			Key = "af-cairo",
			City = "Cairo",
			Region = "Egypt",
			Group = ServerRegion.Africa,
			Status = ServerStatusTier.Experimental,
			MatchTokens = new string[1] { "cairo" },
			Lat = 30.0444,
			Lon = 31.2357
		}
	};

	public static RobloxServerLocation? FindByKey(string? key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return null;
		}
		return All.FirstOrDefault((RobloxServerLocation s) => s.Key == key);
	}
}
