using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Voidstrap.Enums;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public class BetterBloxDataCenterConsoleViewModel : NotifyPropertyChangedViewModel
{
	private static readonly HttpClient _http = Voidstrap.Utility.VpnHttpClient.Create();

	public ObservableCollection<DataCenterModel> DatacenterCollection { get; } = new ObservableCollection<DataCenterModel>();

	public string ErrorMessage { get; private set; } = "";

	public GenericTriState LoadState { get; private set; } = GenericTriState.Unknown;

	public BetterBloxDataCenterConsoleViewModel()
	{
		LoadDatacentersAsync();
	}

	private async Task LoadDatacentersAsync()
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_http, "https://api.betterroblox.com/servers/datacenters"));
			JsonElement rootElement = jsonDocument.RootElement;
			DatacenterCollection.Clear();
			foreach (JsonProperty item in rootElement.EnumerateObject())
			{
				JsonElement value = item.Value;
				JsonElement property = value.GetProperty("location");
				DatacenterCollection.Add(new DataCenterModel
				{
					Id = value.GetProperty("id").GetInt32(),
					Datacenter = (property.GetProperty("datacenter").GetString() ?? "Unknown"),
					City = (property.GetProperty("city").GetString() ?? "Unknown"),
					Region = (property.GetProperty("region").GetString() ?? "Unknown"),
					Country = (property.GetProperty("country").GetString() ?? "Unknown"),
					Organization = (value.GetProperty("organization").GetString() ?? "Unknown")
				});
			}
			LoadState = GenericTriState.Successful;
			OnPropertyChanged("DatacenterCollection");
			OnPropertyChanged("LoadState");
		}
		catch (Exception ex)
		{
			LoadState = GenericTriState.Failed;
			ErrorMessage = "Error loading datacenters: " + ex.Message;
			OnPropertyChanged("LoadState");
			OnPropertyChanged("ErrorMessage");
		}
	}
}
