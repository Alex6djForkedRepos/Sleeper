﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using cpap_app.Configuration;
using cpap_app.Events;
using cpap_app.ViewModels;

using cpaplib;

namespace cpap_app.Views;

public partial class SignalChartContainer : UserControl
{
	private List<SignalChart> _charts = new();

	#region Constructor 
	
	public SignalChartContainer()
	{
		InitializeComponent();

		List<SignalChartConfiguration> signalConfigs = SignalChartConfigurationStore.GetSignalConfigurations();
		List<EventMarkerConfiguration> eventConfigs  = EventMarkerConfigurationStore.GetEventMarkerConfigurations();

		foreach( var config in signalConfigs )
		{
			if( !config.IsVisible )
			{
				continue;
			}

			var chart = new SignalChart() { ChartConfiguration = config, MarkerConfiguration = eventConfigs };

			if( !string.IsNullOrEmpty( config.SecondarySignalName ) )
			{
				var secondaryConfig = signalConfigs.FirstOrDefault( x => x.SignalName.Equals( config.SecondarySignalName, StringComparison.OrdinalIgnoreCase ) );
				if( secondaryConfig != null )
				{
					chart.SecondaryConfiguration = secondaryConfig;
				}
			}

			_charts.Add( chart );

			if( config.IsPinned )
			{
				PinnedCharts.Children.Add( chart );
			}
			else
			{
				UnPinnedCharts.Children.Add( chart );
			}
		}
	}
	
	#endregion
	
	#region Base class overrides

	protected override void OnLoaded( RoutedEventArgs e )
	{
		base.OnLoaded( e );
		
		AddHandler( SignalChart.PinButtonClickedEvent, Chart_PinButtonClicked);
		AddHandler( SignalChart.ChartDraggedEvent, Chart_Dragged);
	}
	#endregion 
	
	#region Event handlers

	private void Chart_Dragged( object? sender, SignalChart.ChartDragEventArgs e )
	{
		if( e.Source is not SignalChart chart || chart.ChartConfiguration == null )
		{
			return;
		}

		var config       = chart.ChartConfiguration;
		var container    = config.IsPinned ? PinnedCharts : UnPinnedCharts;
		var controlIndex = container.Children.IndexOf( chart );
		
		switch( e.Direction )
		{
			case < 0 when controlIndex == 0:
				return;
			case > 0 when controlIndex == container.Children.Count - 1:
				return;
			case < 0:
			{
				var swap = container.Children[ controlIndex - 1 ] as SignalChart;
				Debug.Assert( swap != null );
				
				var updatedConfigs = SignalChartConfigurationStore.SwapDisplayOrder( chart.ChartConfiguration, swap.ChartConfiguration! );
				UpdateConfigurations( updatedConfigs );
				
				container.Children.Move( controlIndex, controlIndex - 1 );
				
				break;
			}
			case > 0:
			{
				var swap = container.Children[ controlIndex + 1 ] as SignalChart;
				Debug.Assert( swap != null );

				var updatedConfigs = SignalChartConfigurationStore.SwapDisplayOrder( chart.ChartConfiguration, swap.ChartConfiguration! );
				UpdateConfigurations( updatedConfigs );
				
				container.Children.Move( controlIndex, controlIndex + 1 );
				
				break;
			}
		}

		Dispatcher.UIThread.Post( chart.BringIntoView, DispatcherPriority.Background );
	}

	private void Chart_PinButtonClicked( object? sender, RoutedEventArgs e )
	{
		if( e.Source is not SignalChart chart )
		{
			return;
		}

		var config = chart.ChartConfiguration;
		if( config == null )
		{
			throw new Exception( $"Unexpected null value on property {nameof( SignalChartConfiguration )}" );
		}
		
		chart.SaveState();

		if( config.IsPinned )
		{
			config.DisplayOrder = 255;
			
			PinnedCharts.Children.Remove( chart );
			UnPinnedCharts.Children.Add( chart );
		}
		else
		{
			config.DisplayOrder = 255;
			
			UnPinnedCharts.Children.Remove( chart );
			PinnedCharts.Children.Add( chart );
		}
		
		chart.RestoreState();
		
		config.IsPinned = !config.IsPinned;

		var configurations = SignalChartConfigurationStore.Update( config );
		UpdateConfigurations( configurations );
	}

	private void ChartDisplayedRangeChanged( object? sender, DateTimeRangeRoutedEventArgs e )
	{
		foreach( var control in _charts.Where( control => control != sender ) )
		{
			control.SetDisplayedRange( e.StartTime, e.EndTime );
		}
	}

	private void ChartOnTimeMarkerChanged( object? sender, DateTimeRoutedEventArgs e )
	{
		foreach( var control in _charts )
		{
			if( control != sender )
			{
				control.UpdateTimeMarker( e.DateTime );
			}
		}
	}

	internal void SelectTimeRange( DateTime startTime, DateTime endTime )
	{
		if( DataContext is not DailyReport || _charts.Count == 0 )
		{
			return;
		}

		foreach( var control in _charts )
		{
			control.SetDisplayedRange( startTime, endTime );
		}
	}
	
	#endregion
	
	#region Public functions

	public void ShowEventType( EventType eventType )
	{
		foreach( var chart in _charts )
		{
			if( chart.ChartConfiguration != null && chart.ChartConfiguration.DisplayedEvents.Contains( eventType ) )
			{
				if( !chart.ChartConfiguration.IsPinned )
				{
					chart.BringIntoView();
				}

				chart.Focus();

				return;
			}
		}
	}
	
	#endregion
	
	#region Private functions 
	
	private void UpdateConfigurations( List<SignalChartConfiguration> configurations )
	{
		foreach( var chart in _charts )
		{
			Debug.Assert( chart.ChartConfiguration != null );
			
			chart.ChartConfiguration = configurations.First( x => x.ID == chart.ChartConfiguration.ID );
		}
	}

	#endregion 
}

