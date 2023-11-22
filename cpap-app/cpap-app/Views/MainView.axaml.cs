using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

using cpap_app.Animation;
using cpap_app.Events;
using cpap_app.Helpers;
using cpap_app.Importers;
using cpap_app.ViewModels;

using cpap_db;

using cpaplib;

using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Media.Animation;
using FluentAvalonia.UI.Windowing;

using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

using OAuth;

namespace cpap_app.Views;

public partial class MainView : UserControl
{
	#region Public events

	public static readonly RoutedEvent<RoutedEventArgs> ImportCpapRequestedEvent =
		RoutedEvent.Register<MainView, RoutedEventArgs>( nameof( ImportCpapRequested ), RoutingStrategies.Bubble );

	public event EventHandler<RoutedEventArgs> ImportCpapRequested
	{
		add => AddHandler( ImportCpapRequestedEvent, value );
		remove => RemoveHandler( ImportCpapRequestedEvent, value );
	}

	public static readonly RoutedEvent<RoutedEventArgs> ImportOximetryRequestedEvent =
		RoutedEvent.Register<MainView, RoutedEventArgs>( nameof( ImportOximetryRequested ), RoutingStrategies.Bubble );

	public event EventHandler<RoutedEventArgs> ImportOximetryRequested
	{
		add => AddHandler( ImportOximetryRequestedEvent, value );
		remove => RemoveHandler( ImportOximetryRequestedEvent, value );
	}

	public static readonly RoutedEvent<DateTimeRoutedEventArgs> LoadDateRequestedEvent =
		RoutedEvent.Register<MainView, DateTimeRoutedEventArgs>( nameof( LoadDateRequested ), RoutingStrategies.Bubble );

	public event EventHandler<DateTimeRoutedEventArgs> LoadDateRequested
	{
		add => AddHandler( LoadDateRequestedEvent, value );
		remove => RemoveHandler( LoadDateRequestedEvent, value );
	}

	#endregion 
	
	#region Public properties

	public static readonly DirectProperty<MainView, UserProfile> ActiveUserProfileProperty =
		AvaloniaProperty.RegisterDirect<MainView, UserProfile>( nameof( ActiveUserProfile ), o => o.ActiveUserProfile );

	public UserProfile ActiveUserProfile
	{
		get => _activeUserProfile;
		set => SetAndRaise( ActiveUserProfileProperty, ref _activeUserProfile, value );
	}
	
	#endregion
	
	#region Private fields

	private UserProfile _activeUserProfile = null!;
	
	#endregion 
	
	#region Constructor 
	
	public MainView()
	{
		InitializeComponent();
		
		NavFrame.IsNavigationStackEnabled = false;
		NavFrame.CacheSize                = 0;

		NavView.SelectedItem = navHome;

		AddHandler( ImportCpapRequestedEvent,     HandleImportRequestCPAP );
		AddHandler( ImportOximetryRequestedEvent, HandleImportRequestOximetry );
		AddHandler( LoadDateRequestedEvent,       HandleLoadDateRequest );

		btnImportCPAP.Tapped      += HandleImportRequestCPAP;
		btnImportOximetry.Tapped  += HandleImportRequestOximetry;
		btnImportGoogleFit.Tapped += HandleImportRequestGoogleFit;
		
		foreach( var importer in OximetryImporterRegistry.RegisteredImporters )
		{
			btnImportOximetry.MenuItems.Add( new NavigationViewItem()
			{
				Content = importer.FriendlyName,
				Tag = importer, 
			} );
		}
		
		ActiveUserProfile = UserProfileStore.GetLastUserProfile();

		navProfile.MenuItemsSource = UserProfileStore.SelectAll();
		
		NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
	}

	#endregion 
	
	#region Base class overrides

	protected override void OnPropertyChanged( AvaloniaPropertyChangedEventArgs change )
	{
		base.OnPropertyChanged( change );

		if( change.Property.Name == nameof( ActiveUserProfile ) )
		{
			if( change.NewValue is UserProfile profile )
			{
				profile.LastLogin = DateTime.Now;
				UserProfileStore.Update( profile );
			}
			
			LoadTabPage( new HomeView() );
		}
	}

	#endregion 
	
	#region Event handlers

	private void UserProfileMenuItemTapped( object? sender, TappedEventArgs e )
	{
		if( sender is Control { Tag: UserProfile profile } )
		{
			ActiveUserProfile = profile;
		}
	}

	private void NavView_OnSelectionChanged( object? sender, NavigationViewSelectionChangedEventArgs e )
	{
		if( sender is not NavigationView navView )
		{
			return;
		}

		if( e.IsSettingsSelected )
		{
			NavFrame.Navigate( typeof( AppSettingsView ), null, e.RecommendedNavigationTransitionInfo );

			return;
		}

		if( e.SelectedItem is NavigationViewItem navViewItem )
		{
			switch( navViewItem.Tag )
			{
				case null:
					return;
				case System.Type pageType:
				{
					var page = Activator.CreateInstance( pageType ) as Control;
					if( page == null )
					{
						throw new InvalidCastException( $"The type {pageType} could not be cast to an appropriate view type" );
					}

					NavigationTransitionInfo transition = new FadeNavigationTransitionInfo();
					
					if( page is DailyReportView dailyReportView )
					{
						dailyReportView.ActiveUserProfile = ActiveUserProfile;
						
						// DailyReportView is way too heavy to deal with complex animations 
						transition = new SuppressNavigationTransitionInfo();
					}
					
					LoadTabPage( page, transition );

					if( object.ReferenceEquals( NavView.SelectedItem, navDailyReport ) )
					{
						navView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
					}
					else if( object.ReferenceEquals( NavView.SelectedItem, navHome ) )
					{
						SetNavViewDisplayMode( NavigationViewPaneDisplayMode.Left );
					}

					return;
				}
				case IOximetryImporter importer:
					HandleImportRequestOximetry( importer );
					break;
			}
		}
	}

	private async void HandleImportRequestGoogleFit( object? sender, TappedEventArgs e )
	{
		var clientConfig = AuthorizationConfigStore.GetConfig();
		if( !clientConfig.IsValid )
		{
			var isConfigured = await ConfigureGoogleFitClient( clientConfig );
			if( !isConfigured )
			{
				return;
			}

			AuthorizationConfigStore.SaveConfig( clientConfig );
		}

		var accessTokenInfo = AccessTokenStore.GetAccessTokenInfo();
		if( !accessTokenInfo.IsValid )
		{
			accessTokenInfo = await AuthorizeGoogleFitClient();
			if( accessTokenInfo is not { IsValid: true } )
			{
				return;
			}

			AccessTokenStore.SaveAccessTokenInfo( accessTokenInfo );
		}

		if( !accessTokenInfo.AccessTokenIsValid )
		{
			var newTokenInfo = await AuthorizationClient.RefreshAuthorizationTokenAsync( clientConfig, accessTokenInfo.RefreshToken );
			if( newTokenInfo.AccessTokenIsValid )
			{
				accessTokenInfo = newTokenInfo;
				AccessTokenStore.SaveAccessTokenInfo( accessTokenInfo );
			}
		}
		
		var appWindow = TopLevel.GetTopLevel( this ) as AppWindow;
		appWindow?.PlatformFeatures.SetTaskBarProgressBarState( TaskBarProgressBarState.Indeterminate );

		var progressDialog = new TaskDialog
		{
			XamlRoot        = appWindow,
			Title           = $"Sync with Google Fit",
			MinWidth        = 500,
			ShowProgressBar = true,
			IconSource      = new SymbolIconSource { Symbol = Symbol.Upload },
			SubHeader       = "Importing sleep information from Google Fit",
			Content         = "Please wait while your data is imported. This may take a few seconds.",
			Buttons =
			{
				TaskDialogButton.CancelButton
			}
		};

		var dataWasImported = false;

		progressDialog.Closing += ( dialog, args ) =>
		{
			appWindow?.PlatformFeatures.SetTaskBarProgressBarState( TaskBarProgressBarState.None );
		};

		progressDialog.Opened += async ( _, _ ) =>
		{
			// Show an animated indeterminate progress bar
			progressDialog.SetProgressBarState( 0, TaskDialogProgressState.Indeterminate );

			var importStartDate = GetImportStartDate( ActiveUserProfile.UserProfileID, SourceType.HealthAPI );
			
			var metaSessions = await GoogleFitImporter.ImportAsync( importStartDate, DateTime.Today.AddDays( 1 ), accessTokenInfo.AccessToken, progressNotify );
			if( metaSessions == null || metaSessions.Count == 0 )
			{
				progressDialog.Hide();
				return;
			}
			
			progressNotify( $"Merging sessions and events..." );

			metaSessions.Sort();

			await Task.Run( async () =>
			{

				var minDate = metaSessions.Min( x => x.StartTime.Date.AddDays( -1 ) );
				var maxDate = metaSessions.Max( x => x.EndTime.Date.AddDays( 1 ) );

				for( var loop = minDate; loop <= maxDate; loop = loop.AddDays( 1 ) )
				{
					// Copy the current date, because otherwise captures might reference the wrong value
					var date = loop;

					Dispatcher.UIThread.Post( () =>
					{
						progressNotify( $"Processing data for \n{date:D}" );
					} );

					using var db = StorageService.Connect();

					var day = db.LoadDailyReport( ActiveUserProfile.UserProfileID, date );
					if( day == null )
					{
						continue;
					}

					// Obtain a list of import data that could potentially be merged with the current day
					var overlappingImports = metaSessions.Where( x => x.CanMergeWith( day ) ).ToList();

					if( overlappingImports.Count == 0 )
					{
						continue;
					}

					bool dayIsModified = false;

					foreach( var data in overlappingImports )
					{
						foreach( var item in data.Items )
						{
							foreach( var session in item.Sessions )
							{
								// If the day doesn't already contain a matching session, add it
								if( !day.Sessions.Any( x => Session.TimesOverlap( x, session ) && x.SourceType == session.SourceType ) )
								{
									// Add the Session to the Day's Session list. 
									addSessionToDay( day, session );

									// Add only the events that happened during this Session. Note that In practice this 
									// should be all of them, since at the time I wrote this all importers only generated
									// a single Session per file, and that is not expected to change, but if it ever does 
									// this code should be robust in handling that change. 
									day.Events.AddRange( item.Events.Where( x => x.StartTime >= session.StartTime && x.StartTime <= session.EndTime ) );

									dayIsModified = true;
								}
							}
						}
					}

					if( dayIsModified )
					{
						db.SaveDailyReport( ActiveUserProfile.UserProfileID, day );

						dataWasImported = true;
					}
				}
				
				Dispatcher.UIThread.Post( () =>
				{
					progressNotify( "Finishing up..." );
				} );
				
				await Task.Delay( 500 );
			} );

			progressDialog.Hide();
		};

		await progressDialog.ShowAsync();
		
		appWindow?.PlatformFeatures.SetTaskBarProgressBarState( TaskBarProgressBarState.None );

		if( !dataWasImported )
		{
			const string upToDate = "All Google Fit data was already up to date.";
			
			var dialog = MessageBoxManager.GetMessageBoxStandard(
				$"Import from Google Fit",
				upToDate,
				ButtonEnum.Ok,
				Icon.Warning );

			await dialog.ShowWindowDialogAsync( this.FindAncestorOfType<Window>() );
		}
		else
		{
			var profileID = ActiveUserProfile.UserProfileID;

			switch( NavFrame.Content )
			{
				case DailyReportView { DataContext: DailyReport dailyReport } dailyReportView:
					// TODO: Because DailyReportView has its own flow for loading a DailyReport, this leaves open the possibility of bypassing things like event subscription, etc.
					dailyReportView.ActiveUserProfile = ActiveUserProfile;
					dailyReportView.DataContext       = LoadDailyReport( profileID, dailyReport.ReportDate.Date );
					break;
				case HomeView homeView:
					homeView.DataContext = LoadDailyReport( profileID, null );
					break;
			}
		}

		return;

		void addSessionToDay( DailyReport day, Session session )
		{
			// Search for a CPAP session that overlaps with but starts before the sleep session.
			// If one is found, we'll extend the sleep session to start at the same time. The 
			// reasoning is that FitBit (and presumably other devices) often don't start recording
			// the session until it detects sleep, but if another session started a few minutes
			// earlier then the user is obviously not asleep at that point, and it's also useful
			// to know how much time was spent awake at the start of each session.
			
			var signal = session.GetSignalByName( SignalNames.SleepStages );
			if( signal != null )
			{
				var matchedSession = day.Sessions
				                        .Where( x =>
					                                x.SourceType is SourceType.CPAP or SourceType.PulseOximetry &&
					                                x.StartTime < signal.StartTime &&
					                                x.EndTime > signal.StartTime )
				                        .MinBy( x => x.StartTime );
				
				if( matchedSession != null )
				{
					var timeDifference  = session.StartTime - matchedSession.StartTime;
					var sampleInterval  = 1.0 / signal.FrequencyInHz;
					int numberOfSamples = (int)Math.Floor( timeDifference.TotalSeconds / sampleInterval );

					for( int i = 0; i < numberOfSamples; i++ )
					{
						signal.Samples.Insert( 0, (int)SleepStage.Awake );
					}

					signal.StartTime  -= TimeSpan.FromSeconds( sampleInterval * numberOfSamples );
					session.StartTime =  DateHelper.Min( session.StartTime, signal.StartTime );
				}
			}

			day.AddSession( session );
		}

		void progressNotify( string message )
		{
			progressDialog.Content = message;
		}
	}

	private static DateTime GetImportStartDate( int userID, SourceType sourceType )
	{
		using var store = StorageService.Connect();

		const string SQL = "SELECT day.ReportDate FROM day JOIN session ON session.dayID == day.ID AND session.SourceType = ? WHERE day.UserProfileID = ? ORDER BY ReportDate DESC LIMIT 1";

		var mostRecentImport = store.Connection.ExecuteScalar<DateTime>( SQL, sourceType, userID ).AsLocalTime();
		mostRecentImport = DateHelper.Max( mostRecentImport, DateTime.Today.AddDays( -30 ) );

		return mostRecentImport;
	}

	private async Task<AccessTokenInfo?> AuthorizeGoogleFitClient()
	{
		AccessTokenInfo? accessTokenInfo    = null;
		string?          authorizationError = null;

		var view = new GoogleFitUserAuthorizationView();

		var dialog = new TaskDialog()
		{
			Title    = string.Empty,
			Buttons  = { TaskDialogButton.CancelButton },
			XamlRoot = (Visual)VisualRoot!,
			Content  = view,
			MaxWidth = 600,
		};

		view.AuthorizationSuccess += ( sender, info ) =>
		{
			accessTokenInfo = info;
			dialog.Hide();
		};
		
		view.AuthorizationError   += ( sender, error ) =>
		{
			authorizationError = error;
			dialog.Hide();
		};

		var dialogResult = await dialog.ShowAsync();
		if( (TaskDialogStandardResult)dialogResult == TaskDialogStandardResult.Cancel )
		{
			return null;
		}

		if( !string.IsNullOrEmpty( authorizationError ) )
		{
			var msgBox = MessageBoxManager.GetMessageBoxStandard(
				$"Error during authorization",
				authorizationError,
				ButtonEnum.Ok,
				Icon.Error
			);
			
			await msgBox.ShowWindowDialogAsync( this.FindAncestorOfType<Window>() );
		}

		return accessTokenInfo;
	}

	private async Task<bool> ConfigureGoogleFitClient( AuthorizationConfig clientConfig )
	{
		var dialog = new TaskDialog()
		{
			Title = $"Google API Client App Configuration",
			Buttons =
			{
				TaskDialogButton.OKButton,
				TaskDialogButton.CancelButton,
			},
			XamlRoot = (Visual)VisualRoot!,
			Content  = new GoogleFitClientConfigView() { DataContext = clientConfig },
			MaxWidth = 500,
		};
		
		var dialogResult = await dialog.ShowAsync();
		return (TaskDialogStandardResult)dialogResult == TaskDialogStandardResult.OK && clientConfig.IsValid;
	}

	private void HandleImportRequestOximetry( object? sender, TappedEventArgs e )
	{
		// TODO: This really needs to be changed so that it's a generic "Import Oximetry CSV File" which selects the importer based on individual files selected
		HandleImportRequestOximetry( OximetryImporterRegistry.RegisteredImporters[ 0 ] );
	}
	
	private async void HandleImportRequestOximetry( IOximetryImporter importer )
	{
		var sp = TopLevel.GetTopLevel( this )?.StorageProvider;
		if( sp == null )
		{
			throw new Exception( $"Failed to get a reference to a {nameof( IStorageProvider )} instance." );
		}

		var filePicker = await sp.OpenFilePickerAsync( new FilePickerOpenOptions()
		{
			Title                  = $"Import from {importer.FriendlyName}",
			SuggestedStartLocation = null,
			AllowMultiple          = true,
			FileTypeFilter         = importer.FileTypeFilters
		} );

		if( filePicker.Count == 0 )
		{
			return;
		}

		var                matcher      = new Regex( importer.FilenameMatchPattern, RegexOptions.IgnoreCase );
		List<MetaSession>  metaSessions = new List<MetaSession>();
		List<ImportedData> importedData = new List<ImportedData>();
		MetaSession?       metaSession  = null;

		var td = new TaskDialog
		{
			XamlRoot        = (Visual)VisualRoot!,
			Title           = $"Import from {importer.FriendlyName}",
			ShowProgressBar = true,
			IconSource      = new SymbolIconSource { Symbol = Symbol.Upload },
			SubHeader       = "Performing Import",
			Content         = "Please wait while your data is imported. This may take a few seconds.",
			Buttons =
			{
				TaskDialogButton.CancelButton
			}
		};

		var appWindow = TopLevel.GetTopLevel( this ) as AppWindow;
		appWindow?.PlatformFeatures.SetTaskBarProgressBarState( TaskBarProgressBarState.Indeterminate );

		bool dataWasImported = false;

		td.Opened += async ( _, _ ) =>
		{
			// Show an animated indeterminate progress bar
			td.SetProgressBarState( 0, TaskDialogProgressState.Indeterminate );

			await Task.Run( async () =>
			{
				for( int i = 0; i < filePicker.Count; i++ )
				{
					var fileItem = filePicker[ i ];
					if( !matcher.IsMatch( fileItem.Name ) )
					{
						// TODO: Need to log import failures and show them to the user
						//Debug.WriteLine( $"File '{fileItem.Name}' does not match the file naming convention for '{importer.FriendlyName}'" );
						continue;
					}

					Dispatcher.UIThread.Post( () =>
					{
						td.Content = $"Scanning data file: \n{fileItem.Name}";
					} );
					
					try
					{
						await using var file = File.OpenRead( fileItem.Path.LocalPath );

						var importOptions        = new PulseOximetryImportOptions() { CalibrationAdjust = -1, TimeAdjust = -60 };
						var eventGeneratorConfig = new OximetryEventGeneratorConfig();

						var data = importer.Load( fileItem.Name, file, importOptions, eventGeneratorConfig );
						if( data is { Sessions.Count: > 0 } )
						{
							importedData.Add( data );
						}
					}
					catch( IOException e )
					{
						// TODO: Need to display a message to the user when a file cannot be opened
						Console.WriteLine( e );
						throw;
					}
				}
				
				// Sort the imported data before grouping into MetaSessions
				importedData.Sort();
				
				// Grouping the imported data into "Meta-sessions" allows us to ensure that sessions that are 
				// separated by up to an hour (on either side of the main group) still get included.
				foreach( var data in importedData )
				{
					if( metaSession == null || !metaSession.CanMerge( data ) )
					{
						metaSession = new MetaSession();
						metaSessions.Add( metaSession );
					}
							
					metaSession.Add( data );
				}

				if( metaSessions.Count > 0 )
				{
					metaSessions.Sort();

					var minDate = metaSessions.Min( x => x.StartTime.Date.AddDays( -1 ) );
					var maxDate = metaSessions.Max( x => x.EndTime.Date.AddDays( 1 ) );

					for( var loop = minDate; loop <= maxDate; loop = loop.AddDays( 1 ) )
					{
						// Copy the current date, because otherwise captures might reference the wrong value
						var date = loop;

						Dispatcher.UIThread.Post( () =>
						{
							td.Content = $"Merging sessions and events for \n{date:D}";
						} );

						using var db  = StorageService.Connect();
						
						var day = db.LoadDailyReport( ActiveUserProfile.UserProfileID, date );
						if( day == null )
						{
							continue;
						}

						// Obtain a list of import data that could potentially be merged with the current day
						var overlappingImports = metaSessions.Where( x => x.CanMergeWith( day ) ).ToList();

						if( overlappingImports.Count == 0 )
						{
							continue;
						}

						bool dayIsModified = false;
						
						foreach( var data in overlappingImports )
						{
							foreach( var item in data.Items )
							{
								foreach( var session in item.Sessions )
								{
									// If the day doesn't already contain a matching session, add it
									if( !day.Sessions.Any( x => Session.TimesOverlap( x, session ) && x.SourceType == session.SourceType ) )
									{
										// Add the Session to the Day's Session list. 
										day.AddSession( session );

										// Add only the events that happened during this Session. Note that In practice this 
										// should be all of them, since at the time I wrote this all importers only generated
										// a single Session per file, and that is not expected to change, but if it ever does 
										// this code should be robust in handling that change. 
										day.Events.AddRange( item.Events.Where( x => x.StartTime >= session.StartTime && x.StartTime <= session.EndTime ) );
										day.Events.Sort();

										dayIsModified = true;
									}
								}
							}
						}

						if( dayIsModified )
						{
							day.UpdateSignalStatistics( SignalNames.SpO2 );
							day.UpdateSignalStatistics( SignalNames.Pulse );

							db.SaveDailyReport( ActiveUserProfile.UserProfileID, day );

							dataWasImported = true;
						}
					}
				}

				Dispatcher.UIThread.Post( () =>
				{
					td.Hide( TaskDialogStandardResult.OK );
					appWindow?.PlatformFeatures.SetTaskBarProgressBarState( TaskBarProgressBarState.None );
				} );
			} );
		};

		await td.ShowAsync();	

		if( metaSessions.Count == 0 || !dataWasImported )
		{
			const string upToDate     = "All pulse oximetry data was already up to date.";
			//const string noMatchFound = "One or more of the files you selected could not be matched to any existing CPAP sessions.";
			
			var dialog = MessageBoxManager.GetMessageBoxStandard(
				$"Import from {importer.FriendlyName}",
				upToDate,
				ButtonEnum.Ok,
				Icon.Warning );

			await dialog.ShowWindowDialogAsync( this.FindAncestorOfType<Window>() );
		}
		else
		{
			var profileID = ActiveUserProfile.UserProfileID;

			switch( NavFrame.Content )
			{
				case DailyReportView { DataContext: DailyReport dailyReport } dailyReportView:
					// TODO: Because DailyReportView has its own flow for loading a DailyReport, this leaves open the possibility of bypassing things like event subscription, etc.
					dailyReportView.ActiveUserProfile = ActiveUserProfile;
					dailyReportView.DataContext       = LoadDailyReport( profileID, dailyReport.ReportDate.Date );
					break;
				case HomeView homeView:
					homeView.DataContext = LoadDailyReport( profileID, null );
					break;
			}
		}
	}

	private void HandleLoadDateRequest( object? sender, DateTimeRoutedEventArgs e )
	{
		var day = LoadDailyReport( ActiveUserProfile.UserProfileID, e.DateTime );
		DataContext = new DailyReportViewModel( day );

		NavView.SelectedItem = navDailyReport;
	}

	private void HandleImportRequestOximetry( object? sender, RoutedEventArgs e )
	{
		// TODO: This really needs to be changed so that it's a generic "Import Oximetry CSV File" which selects the importer based on individual files selected
		HandleImportRequestOximetry( OximetryImporterRegistry.RegisteredImporters[ 0 ] );
	}
	
	private async void HandleImportRequestCPAP( object? sender, RoutedEventArgs e )
	{
		string importPath = string.Empty;

		var drives = DriveInfo.GetDrives();
		foreach( var drive in drives )
		{
			if( !drive.IsReady )
			{
				continue;
			}

			if( ResMedDataLoader.HasCorrectFolderStructure( drive.RootDirectory.FullName ) )
			{
				var machineID = ResMedDataLoader.LoadMachineIdentificationInfo( drive.RootDirectory.FullName );

				var dialog = MessageBoxManager.GetMessageBoxStandard(
					$"Import from {drive.RootDirectory}?",
					$"There appears to be a CPAP data folder structure on Drive {drive.Name}\nMachine: {machineID.ProductName}, Serial #: {machineID.SerialNumber}\n\nDo you want to import this data from {drive.Name}?",
					ButtonEnum.YesNoCancel,
					Icon.Database );

				var result = await dialog.ShowWindowDialogAsync( this.FindAncestorOfType<Window>() );

				if( result == ButtonResult.Cancel )
				{
					return;
				}

				if( result == ButtonResult.Yes )
				{
					importPath = drive.RootDirectory.FullName;
					break;
				}
			}
		}

		if( string.IsNullOrEmpty( importPath ) )
		{
			var sp = TopLevel.GetTopLevel( this )?.StorageProvider;
			if( sp == null )
			{
				throw new Exception( $"Failed to get a reference to a {nameof( IStorageProvider )} instance." );
			}

			var folder = await sp.OpenFolderPickerAsync( new FolderPickerOpenOptions
			{
				Title                  = "CPAP Data Import - Select the folder containing your CPAP data",
				SuggestedStartLocation = null,
				AllowMultiple          = false
			} );

			if( folder.Count == 0 )
			{
				return;
			}

			importPath = folder[ 0 ].Path.LocalPath;
		}

		if( !Directory.Exists( importPath ) )
		{
			return;
		}

		if( !ResMedDataLoader.HasCorrectFolderStructure( importPath ) )
		{
			var dialog = MessageBoxManager.GetMessageBoxStandard(
				$"Import from folder",
				$"Folder {importPath} does not appear to contain CPAP data",
				ButtonEnum.Ok,
				Icon.Database );

			await dialog.ShowWindowDialogAsync( this.FindAncestorOfType<Window>() );
			return;
		}

		var td = new TaskDialog
		{
			XamlRoot        = this.FindAncestorOfType<Window>(),
			Title           = "Import CPAP Data",
			ShowProgressBar = true,
			IconSource      = new SymbolIconSource { Symbol = Symbol.Upload },
			SubHeader       = "Performing Import",
			Content         = "Please wait while your CPAP data is imported. This may take a few seconds.",
			Buttons =
			{
				TaskDialogButton.CancelButton
			}
		};

		var appWindow = TopLevel.GetTopLevel( this ) as AppWindow;
		appWindow?.PlatformFeatures.SetTaskBarProgressBarState( TaskBarProgressBarState.Indeterminate );

		td.Opened += async ( _, _ ) =>
		{
			// Show an animated indeterminate progress bar
			td.SetProgressBarState( 0, TaskDialogProgressState.Indeterminate );

			var startTime = Environment.TickCount;
		
			await Task.Run( async () =>
			{
				var mostRecentDay = ImportFrom( importPath );
		
				// Sounds cheesy, but showing a progress bar for even a second serves to show 
				// the user that the work was performed. It's otherwise often too fast for them 
				// to notice, and they're left with the feeling that "it didn't work" because
				// they didn't see any visual feedback. 
				await Task.Delay( Math.Max( 0, 1000 - (Environment.TickCount - startTime) ) );

				Dispatcher.UIThread.Post( onImportComplete );
				
				return;

				async void onImportComplete()
				{
					td.Hide( TaskDialogStandardResult.OK );

					if( mostRecentDay != null )
					{
						var profileID = ActiveUserProfile.UserProfileID;

						//NavView.SelectedItem = navDailyReport;
						// TODO: Because DailyReportView has its own flow for loading a DailyReport, this leaves open the possibility of bypassing things like event subscription, etc.
						DataContext = LoadDailyReport( profileID, mostRecentDay.Value );
					}
					else
					{
						var dialog = MessageBoxManager.GetMessageBoxStandard( $"Import from folder", $"All CPAP data was already up to date.", ButtonEnum.Ok, Icon.Database );

						await dialog.ShowWindowDialogAsync( this.FindAncestorOfType<Window>() );
					}

					appWindow?.PlatformFeatures.SetTaskBarProgressBarState( TaskBarProgressBarState.None );
				}
			} );
		};

		td.XamlRoot = (Visual)VisualRoot!;
		await td.ShowAsync();	
	}

	#endregion 
	
	#region Private functions

	private static DailyReport LoadDailyReport( int profileId, DateTime? date )
	{
		using var store = StorageService.Connect();

		var day = store.LoadDailyReport( profileId, date ?? store.GetMostRecentStoredDate( profileId ) );

		return day;
	}
	
	private void LoadTabPage( Control page, NavigationTransitionInfo? transition = null )
	{
		transition ??= new FadeNavigationTransitionInfo();
		
		NavFrame.Content = page;
		
		Dispatcher.UIThread.Post( () =>
		{
			if( NavFrame.Content is InputElement control )
			{
				control.Focusable = true;
				control.Focus();
			}
		}, DispatcherPriority.Loaded );

		// Page transitions disabled. They look neat, but don't really work well for pages with a lot of layout on slow machines. 
		/*
		// Post the animation otherwise pages that take slightly longer to load won't
		// have an animation since it will run before layout is complete
		Dispatcher.UIThread.Post( () =>
		{
			transition.RunAnimation( page, CancellationToken.None );
		}, DispatcherPriority.Render );
		*/
		
		Dispatcher.UIThread.Post( () =>
		{
			GC.Collect( 0 );
		}, DispatcherPriority.Background );
	}

	private void SetNavViewDisplayMode( NavigationViewPaneDisplayMode mode )
	{
		Dispatcher.UIThread.Post( () =>
		{
			NavView.PaneDisplayMode = mode;
		}, DispatcherPriority.Background );
	}

	private DateTime? ImportFrom( string folder )
	{
		using var storage = StorageService.Connect();
		storage.Connection.BeginTransaction();

		try
		{
			int startTime = Environment.TickCount;

			var profileID = ActiveUserProfile.UserProfileID;

			var mostRecentDay = storage.GetMostRecentStoredDate( profileID ).AddHours( 12 );

			var loader = new ResMedDataLoader();
			var days   = loader.LoadFromFolder( folder, mostRecentDay );

			if( days == null || days.Count == 0 )
			{
				storage.Connection.Commit();
				return null;
			}

			foreach( var day in days )
			{
				storage.SaveDailyReport( profileID, day );
			}

			storage.Connection.Commit();

			var elapsed = Environment.TickCount - startTime;
			Debug.WriteLine( $"Time to load CPAP data ({days.Count} days): {elapsed / 1000.0f:F3} seconds" );
			
			return storage.GetMostRecentStoredDate( profileID );
		}
		catch
		{
			storage.Connection.Rollback();
			throw;
		}
	}
	
	#endregion
}
