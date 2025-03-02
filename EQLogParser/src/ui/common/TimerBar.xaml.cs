﻿using Syncfusion.UI.Xaml.ProgressBar;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TimerBar.xaml
  /// </summary>
  public partial class TimerBar : UserControl
  {
    private string OverlayId;
    private bool? Active = true;

    public TimerBar()
    {
      InitializeComponent();
    }

    internal void Init(string overlayId)
    {
      OverlayId = overlayId;
      progress.SetResourceReference(ProgressBarBase.TrackColorProperty, "TimerBarTrackColor-" + OverlayId);
      progress.SetResourceReference(HeightProperty, "TimerBarHeight-" + OverlayId);
      progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarActiveColor-" + OverlayId);
      time.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + OverlayId);
      title.SetResourceReference(TextBlock.FontSizeProperty, "TimerBarFontSize-" + OverlayId);
      time.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + OverlayId);
      title.SetResourceReference(TextBlock.ForegroundProperty, "TimerBarFontColor-" + OverlayId);
    }

    internal void Update(string displayName, string timeText, double remaining)
    {
      title.Text = displayName;
      time.Text = timeText;
      progress.Progress = remaining;
    }

    internal void SetActive()
    {
      if (Active != true)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarActiveColor-" + OverlayId);
        Active = true;
      }
    }

    internal void SetReset()
    {
      if (Active != false)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarResetColor-" + OverlayId);
        Active = false;
      }
    }

    internal void SetIdle()
    {
      if (Active != null)
      {
        progress.SetResourceReference(ProgressBarBase.ProgressColorProperty, "TimerBarIdleColor-" + OverlayId);
        Active = null;
      }
    }
  }
}
