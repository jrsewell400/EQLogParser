﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace EQLogParser
{
  internal class TriggerManager
  {
    internal event EventHandler<bool> EventsUpdateTree;
    internal event EventHandler<Trigger> EventsSelectTrigger;
    internal event EventHandler<dynamic> EventsAddText;
    internal event EventHandler<Trigger> EventsNewTimer;
    private const string TRIGGERS_FILE = "triggers.json";
    private static readonly long COUNT_TIME = TimeSpan.TicksPerMillisecond * 750;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static object CollectionLock = new object();
    private static object LockObject = new object();
    private readonly ObservableCollection<dynamic> AlertLog = new ObservableCollection<dynamic>();
    private readonly List<TimerData> ActiveTimers = new List<TimerData>();
    private readonly DispatcherTimer TriggerUpdateTimer;
    private readonly TriggerNode TriggerNodes;
    private Channel<dynamic> LogChannel = null;
    private string CurrentVoice;
    private int CurrentVoiceRate;
    private Task RefreshTask = null;
    internal static TriggerManager Instance = new TriggerManager();

    public TriggerManager()
    {
      BindingOperations.EnableCollectionSynchronization(AlertLog, CollectionLock);

      var json = ConfigUtil.ReadConfigFile(TRIGGERS_FILE);
      if (json != null)
      {
        try
        {
          TriggerNodes = JsonSerializer.Deserialize<TriggerNode>(json, new JsonSerializerOptions { IncludeFields = true });
        }
        catch (Exception ex)
        {
          LOG.Error("Error Parsing " + TRIGGERS_FILE, ex);
          TriggerNodes = new TriggerNode();
        }
      }
      else
      {
        TriggerNodes = new TriggerNode();
      }

      CurrentVoice = TriggerUtil.GetSelectedVoice();
      CurrentVoiceRate = TriggerUtil.GetVoiceRate();

      TriggerUpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      TriggerUpdateTimer.Tick += TriggerDataUpdated;
    }

    internal void Init() => (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
    internal ObservableCollection<dynamic> GetAlertLog() => AlertLog;
    private string ModText(string text) => string.IsNullOrEmpty(text) ? null : text.Replace("{c}", ConfigUtil.PlayerName, StringComparison.OrdinalIgnoreCase);
    internal void SetVoice(string voice) => CurrentVoice = voice;
    internal void SetVoiceRate(int rate) => CurrentVoiceRate = rate;
    internal void Select(Trigger trigger) => EventsSelectTrigger?.Invoke(this, trigger);

    internal List<TimerData> GetActiveTimers()
    {
      lock (ActiveTimers)
      {
        return ActiveTimers.ToList();
      }
    }

    internal TriggerTreeViewNode GetTriggerTreeView()
    {
      lock (TriggerNodes)
      {
        return TriggerUtil.GetTreeView(TriggerNodes, "Triggers");
      }
    }

    internal bool IsActive()
    {
      lock (LockObject)
      {
        return (LogChannel != null);
      }
    }

    internal void AddAction(LineData lineData)
    {
      lock (LockObject)
      {
        LogChannel?.Writer.WriteAsync(lineData);
      }

      if (!double.IsNaN(lineData.BeginTime) && ConfigUtil.IfSetOrElse("TriggersWatchForGINA", false))
      {
        TriggerUtil.CheckGina(lineData);
      }
    }

    internal void MergeTriggers(List<TriggerNode> list, TriggerNode parent)
    {
      lock (TriggerNodes)
      {
        foreach (var node in list)
        {
          TriggerUtil.DisableNodes(node);
          TriggerUtil.MergeNodes(node.Nodes, parent, true);
        }
      }

      SaveTriggers();
      RequestRefresh();
      EventsUpdateTree?.Invoke(this, true);
    }

    internal void MergeTriggers(TriggerNode newTriggers, string newFolder)
    {
      newFolder += " (" + DateUtil.FormatSimpleDate(DateUtil.ToDouble(DateTime.Now)) + ")";
      newTriggers.Name = newFolder;

      lock (TriggerNodes)
      {
        TriggerNodes.Nodes.Add(newTriggers);
      }

      SaveTriggers();
      RequestRefresh();
      EventsUpdateTree?.Invoke(this, true);
    }

    internal void MergeTriggers(TriggerNode newTriggers,  bool doSort, TriggerNode parent = null)
    {
      lock (TriggerNodes)
      {
        TriggerUtil.MergeNodes(newTriggers.Nodes, (parent == null) ? TriggerNodes : parent, doSort);
      }

      SaveTriggers();
      RequestRefresh();
      EventsUpdateTree?.Invoke(this, true);
    }

    internal void UpdateTriggers(bool needRefresh = true)
    {
      TriggerUpdateTimer.Stop();
      TriggerUpdateTimer.Start();

      if (needRefresh)
      {
        TriggerUpdateTimer.Tag = needRefresh;
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Start()
    {
      LOG.Info("Starting Trigger Manager");
      try
      {
        var synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
        return;
      }

      var lowPriChannel = Channel.CreateUnbounded<LowPriData>();
      var speechChannel = Channel.CreateUnbounded<Speak>();
      StartSpeechReader(speechChannel);
      TriggerOverlayManager.Instance.Start();
      Channel<dynamic> logChannel;

      lock (LockObject)
      {
        LogChannel?.Writer.Complete();
        logChannel = LogChannel = Channel.CreateUnbounded<dynamic>();
      }

      _ = Task.Run(async () =>
      {
        LinkedList<TriggerWrapper> activeTriggers = null;

        try
        {
          activeTriggers = GetActiveTriggers();

          while (await logChannel.Reader.WaitToReadAsync())
          {
            var result = await logChannel.Reader.ReadAsync();

            if (result is LinkedList<TriggerWrapper> updatedTriggers)
            {
              lock (activeTriggers)
              {
                activeTriggers.ToList().ForEach(wrapper => CleanupWrapper(wrapper));
              }

              activeTriggers = updatedTriggers;
            }
            else if (result is LineData lineData)
            {
              LinkedListNode<TriggerWrapper> node = null;

              lock (activeTriggers)
              {
                node = activeTriggers.First;
              }

              while (node != null)
              {
                // save since the nodes may get reordered
                var nextNode = node.Next;

                // if within a month assume handle it right away
                // millis in 30 days
                if ((new TimeSpan(DateTime.Now.Ticks).TotalMilliseconds - node.Value.TriggerData.LastTriggered) <= 2592000000)
                {
                  HandleTrigger(activeTriggers, node, lineData, speechChannel);
                }
                else
                {
                  _ = lowPriChannel.Writer.WriteAsync(new LowPriData
                  {
                    ActiveTriggers = activeTriggers,
                    LineData = lineData,
                    Node = node,
                    SpeechChannel = speechChannel
                  });
                }

                node = nextNode;
              }
            }
          }
        }
        catch (Exception ex)
        {
          // channel closed
          LOG.Debug(ex);
        }

        lowPriChannel?.Writer.Complete();
        speechChannel?.Writer.Complete();

        lock (activeTriggers)
        {
          activeTriggers.ToList().ForEach(wrapper => CleanupWrapper(wrapper));
        }
      });

      _ = Task.Run(async () =>
      {
        try
        {
          while (await lowPriChannel.Reader.WaitToReadAsync())
          {
            var result = await lowPriChannel.Reader.ReadAsync();
            HandleTrigger(result.ActiveTriggers, result.Node, result.LineData, result.SpeechChannel);
          }
        }
        catch (Exception)
        {
          // end channel
        }
      });

      (Application.Current.MainWindow as MainWindow).ShowTriggersEnabled(true);
      ConfigUtil.SetSetting("TriggersEnabled", true.ToString(CultureInfo.CurrentCulture));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    internal void Stop(bool save = true)
    {
      LOG.Info("Shutting Down Trigger Manager");
      lock (LockObject)
      {
        LogChannel?.Writer.Complete();
        LogChannel = null;
      }

      TriggerOverlayManager.Instance.Stop();

      SaveTriggers();
      (Application.Current.MainWindow as MainWindow)?.ShowTriggersEnabled(false);

      if (save)
      {
        ConfigUtil.SetSetting("TriggersEnabled", false.ToString(CultureInfo.CurrentCulture));
      }
    }

    private void HandleTrigger(LinkedList<TriggerWrapper> activeTriggers, LinkedListNode<TriggerWrapper> node,
      LineData lineData, Channel<Speak> speechChannel)
    {
      var time = long.MinValue;
      var start = DateTime.Now.Ticks;
      var action = lineData.Action;
      MatchCollection matches = null;
      bool found = false;

      lock (node.Value)
      {
        var wrapper = node.Value;
        if (wrapper.Regex != null)
        {
          matches = wrapper.Regex.Matches(action);
          found = matches != null && matches.Count > 0 && TriggerUtil.CheckNumberOptions(wrapper.RegexNOptions, matches);
        }
        else if (!string.IsNullOrEmpty(wrapper.ModifiedPattern))
        {
          found = action.Contains(wrapper.ModifiedPattern, StringComparison.OrdinalIgnoreCase);
        }

        if (found)
        {
          var beginTicks = DateTime.Now.Ticks;
          node.Value.TriggerData.LastTriggered = new TimeSpan(beginTicks).TotalMilliseconds;

          time = (long)((beginTicks - start) / 10);
          wrapper.TriggerData.WorstEvalTime = Math.Max(time, wrapper.TriggerData.WorstEvalTime);

          if (ProcessText(wrapper.ModifiedTimerName, matches) is string displayName && !string.IsNullOrEmpty(displayName))
          {
            if (wrapper.TimerCounts.TryGetValue(displayName, out var timerCount))
            {
              if ((beginTicks - timerCount.CountTicks) > COUNT_TIME)
              {
                timerCount.Count = 1;
                timerCount.CountTicks = beginTicks;
              }
              else
              {
                timerCount.Count++;
              }
            }
            else
            {
              wrapper.TimerCounts[displayName] = new TimerCount { Count = 1, CountTicks = beginTicks };
            }

            if (wrapper.TriggerData.TimerType > 0 && wrapper.TriggerData.DurationSeconds > 0)
            {
              StartTimer(wrapper, displayName, beginTicks, speechChannel, lineData.Line, matches);
            }
          }

          var speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.SoundToPlay, wrapper.ModifiedSpeak, out bool isSound);
          if (!string.IsNullOrEmpty(speak))
          {
            speechChannel.Writer.WriteAsync(new Speak
            {
              Trigger = wrapper.TriggerData,
              TTSOrSound = speak,
              IsSound = isSound,
              Matches = matches
            });
          }

          AddTextEvent(wrapper.ModifiedDisplay, wrapper.TriggerData, matches);
          AddEntry(lineData.Line, wrapper.TriggerData, "Trigger", time);
        }
        else
        {
          wrapper.TimerList.ToList().ForEach(timerData =>
          {
            MatchCollection earlyMatches;
            var endEarly = CheckEndEarly(timerData.EndEarlyRegex, timerData.EndEarlyRegexNOptions, timerData.EndEarlyPattern, action, out earlyMatches);

            // try 2nd
            if (!endEarly)
            {
              endEarly = CheckEndEarly(timerData.EndEarlyRegex2, timerData.EndEarlyRegex2NOptions, timerData.EndEarlyPattern2, action, out earlyMatches);
            }

            if (endEarly)
            {           
              bool isSound;
              string speak = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndEarlySoundToPlay, wrapper.ModifiedEndEarlySpeak, out isSound);
              speak = string.IsNullOrEmpty(speak) ? TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out isSound) : speak;
              string displayText = string.IsNullOrEmpty(wrapper.ModifiedEndEarlyDisplay) ? wrapper.ModifiedEndDisplay : wrapper.ModifiedEndEarlyDisplay;

              speechChannel.Writer.WriteAsync(new Speak
              {
                Trigger = wrapper.TriggerData,
                TTSOrSound = speak,
                IsSound = isSound,
                Matches = earlyMatches,
                OriginalMatches = timerData.OriginalMatches
              });

              AddTextEvent(displayText, wrapper.TriggerData, earlyMatches, timerData.OriginalMatches);
              AddEntry(lineData.Line, wrapper.TriggerData, "Timer End Early");
              CleanupTimer(wrapper, timerData);
            }
          });
        }
      }
    }

    private bool CheckEndEarly(Regex endEarlyRegex, List<NumberOptions> options, string endEarlyPattern,
      string action, out MatchCollection earlyMatches)
    {
      earlyMatches = null;
      bool endEarly = false;

      if (endEarlyRegex != null)
      {
        earlyMatches = endEarlyRegex.Matches(action);
        if (earlyMatches != null && earlyMatches.Count > 0 && TriggerUtil.CheckNumberOptions(options, earlyMatches))
        {
          endEarly = true;
        }
      }
      else if (!string.IsNullOrEmpty(endEarlyPattern))
      {
        if (action.Contains(endEarlyPattern, StringComparison.OrdinalIgnoreCase))
        {
          endEarly = true;
        }
      }

      return endEarly;
    }

    private void StartSpeechReader(Channel<Speak> speechChannel)
    {
      var task = Task.Run(async () =>
      {
        SpeechSynthesizer synth = null;
        SoundPlayer player = null;

        try
        {
          synth = new SpeechSynthesizer();
          synth.SetOutputToDefaultAudioDevice();
          player = new SoundPlayer();
          Trigger previous = null;

          while (await speechChannel.Reader.WaitToReadAsync())
          {
            var result = await speechChannel.Reader.ReadAsync();
            if (!string.IsNullOrEmpty(result.TTSOrSound))
            {
              var cancel = (result.Trigger.Priority < previous?.Priority);
              if (cancel && synth.State == SynthesizerState.Speaking)
              {               
                synth.SpeakAsyncCancelAll();
                AddEntry("", previous, "Speech Canceled");
              }

              if (result.IsSound)
              {
                try
                {
                  if (cancel)
                  {
                    player?.Stop();
                    AddEntry("", previous, "Wav Canceled");
                  }

                  var theFile = @"data\sounds\" + result.TTSOrSound;
                  if (player.SoundLocation != theFile && File.Exists(theFile))
                  {
                    player.SoundLocation = theFile;
                  }

                  if (!string.IsNullOrEmpty(player.SoundLocation))
                  {
                    player.Play();
                  }
                }
                catch (Exception)
                {
                  // ignore
                }
              }
              else
              {
                var speak = ProcessText(result.TTSOrSound, result.OriginalMatches);
                speak = ProcessText(speak, result.Matches);

                if (!string.IsNullOrEmpty(CurrentVoice) && synth.Voice.Name != CurrentVoice)
                {
                  synth.SelectVoice(CurrentVoice);
                }

                if (CurrentVoiceRate != synth.Rate)
                {
                  synth.Rate = CurrentVoiceRate;
                }

                synth.SpeakAsync(speak);
              }
            }

            previous = result.Trigger;
          }
        }
        catch (Exception ex)
        {
          // channel closed
          LOG.Debug(ex);
        }
        finally
        {
          synth?.Dispose();
          player?.Dispose();
        }
      });
    }

    private void StartTimer(TriggerWrapper wrapper, string displayName, long beginTicks, Channel<Speak> speechChannel, string line, MatchCollection matches)
    {
      var trigger = wrapper.TriggerData;

      // Restart Timer Option so clear out everything
      if (trigger.TriggerAgainOption == 1)
      {
        CleanupWrapper(wrapper);
      }
      else if (trigger.TriggerAgainOption == 2)
      {
        if (wrapper.TimerList.ToList().FirstOrDefault(timerData => displayName.Equals(timerData?.DisplayName, StringComparison.OrdinalIgnoreCase))
          is TimerData timerData)
        {
          CleanupTimer(wrapper, timerData);
        }
      }

      // Start a New independent Timer as long as one is not already running when Option 3 is selected
      // Option 3 is to Do Nothing when a 2nd timer is triggered so you onlu have the original timer running
      if (!(trigger.TriggerAgainOption == 3 && wrapper.TimerList.Count > 0))
      {
        TimerData newTimerData = null;
        if (trigger.WarningSeconds > 0 && trigger.DurationSeconds - trigger.WarningSeconds is double diff && diff > 0)
        {
          newTimerData = new TimerData { DisplayName = displayName, WarningSource = new CancellationTokenSource() };

          Task.Delay((int)diff * 1000).ContinueWith(task =>
          {
            var proceed = false;
            lock (wrapper)
            {
              if (newTimerData.WarningSource != null)
              {
                proceed = !newTimerData.WarningSource.Token.IsCancellationRequested;
                newTimerData.WarningSource.Dispose();
                newTimerData.WarningSource = null;
              }

              if (proceed)
              {
                var speak = TriggerUtil.GetFromDecodedSoundOrText(trigger.WarningSoundToPlay, wrapper.ModifiedWarningSpeak, out bool isSound);

                speechChannel.Writer.WriteAsync(new Speak
                {
                  Trigger = trigger,
                  TTSOrSound = speak,
                  IsSound = isSound,
                  Matches = matches
                });

                AddTextEvent(wrapper.ModifiedWarningDisplay, trigger, matches);
                AddEntry(line, trigger, "Timer Warning");
              }
            }
          }, newTimerData.WarningSource.Token);
        }

        if (newTimerData == null)
        {
          newTimerData = new TimerData { DisplayName = displayName };
        }

        if (wrapper.HasRepeated)
        {
          newTimerData.Repeated = wrapper.TimerCounts[displayName].Count;
        }

        newTimerData.EndTicks = beginTicks + (long)(TimeSpan.TicksPerMillisecond * trigger.DurationSeconds * 1000);
        newTimerData.DurationTicks = newTimerData.EndTicks - beginTicks;
        newTimerData.ResetTicks = trigger.ResetDurationSeconds > 0 ?
          beginTicks + (long)(TimeSpan.TicksPerSecond * trigger.ResetDurationSeconds) : 0;
        newTimerData.ResetDurationTicks = newTimerData.ResetTicks - beginTicks;
        newTimerData.SelectedOverlays = trigger.SelectedOverlays.ToList();
        newTimerData.TriggerAgainOption = trigger.TriggerAgainOption;
        newTimerData.TimerType = trigger.TimerType;
        newTimerData.OriginalMatches = matches;
        newTimerData.Key = trigger.Name + "-" + trigger.Pattern;
        newTimerData.CancelSource = new CancellationTokenSource();

        if (!string.IsNullOrEmpty(trigger.EndEarlyPattern))
        {
          var endEarlyPattern = ProcessText(trigger.EndEarlyPattern, matches);
          endEarlyPattern = UpdatePattern(trigger.EndUseRegex, ConfigUtil.PlayerName, endEarlyPattern, out List<NumberOptions> numberOptions2);

          if (trigger.EndUseRegex)
          {           
            newTimerData.EndEarlyRegex = new Regex(endEarlyPattern, RegexOptions.IgnoreCase);
            newTimerData.EndEarlyRegexNOptions = numberOptions2;
          }
          else
          {
            newTimerData.EndEarlyPattern = endEarlyPattern;
          }
        }

        if (!string.IsNullOrEmpty(trigger.EndEarlyPattern2))
        {
          var endEarlyPattern2 = ProcessText(trigger.EndEarlyPattern2, matches);
          endEarlyPattern2 = UpdatePattern(trigger.EndUseRegex2, ConfigUtil.PlayerName, endEarlyPattern2, out List<NumberOptions> numberOptions3);

          if (trigger.EndUseRegex2)
          {
            newTimerData.EndEarlyRegex2 = new Regex(endEarlyPattern2, RegexOptions.IgnoreCase);
            newTimerData.EndEarlyRegex2NOptions = numberOptions3;
          }
          else
          {
            newTimerData.EndEarlyPattern2 = endEarlyPattern2;
          }
        }

        wrapper.TimerList.Add(newTimerData);
        bool needEvent = wrapper.TimerList.Count == 1;

        Task.Delay((int)(trigger.DurationSeconds * 1000)).ContinueWith(task =>
        {
          var proceed = false;
          lock (wrapper)
          {
            if (newTimerData.CancelSource != null)
            {
              proceed = !newTimerData.CancelSource.Token.IsCancellationRequested;
            }

            if (proceed)
            {
              var speak = TriggerUtil.GetFromDecodedSoundOrText(trigger.EndSoundToPlay, wrapper.ModifiedEndSpeak, out bool isSound);
              speechChannel.Writer.WriteAsync(new Speak
              {
                Trigger = trigger,
                TTSOrSound = speak,
                IsSound = isSound,
                Matches = matches,
                OriginalMatches = newTimerData.OriginalMatches
              });

              AddTextEvent(wrapper.ModifiedEndDisplay, trigger, matches, newTimerData.OriginalMatches);
              AddEntry(line, trigger, "Timer End");
              CleanupTimer(wrapper, newTimerData);
            }
          }
        }, newTimerData.CancelSource.Token);

        lock (ActiveTimers)
        {
          ActiveTimers.Add(newTimerData);
        }

        if (needEvent)
        {
          Application.Current.Dispatcher.InvokeAsync(() => EventsNewTimer?.Invoke(this, trigger), DispatcherPriority.Render);
        }
      }
    }

    private void CleanupTimer(TriggerWrapper wrapper, TimerData timerData)
    {
      timerData.CancelSource?.Cancel();
      timerData.CancelSource?.Dispose();
      timerData.WarningSource?.Cancel();
      timerData.WarningSource?.Dispose();
      timerData.CancelSource = null;
      timerData.WarningSource = null;
      wrapper.TimerList.Remove(timerData);

      lock (ActiveTimers)
      {
        ActiveTimers.Remove(timerData);
      }
    }

    private void CleanupWrapper(TriggerWrapper wrapper)
    {
      lock (wrapper)
      {
        wrapper.TimerList.ToList().ForEach(timerData => CleanupTimer(wrapper, timerData));
      }
    }

    private string ProcessText(string text, MatchCollection matches)
    {
      if (matches != null && !string.IsNullOrEmpty(text))
      {
        foreach (Match match in matches)
        {
          for (int i = 1; i < match.Groups.Count; i++)
          {
            if (!string.IsNullOrEmpty(match.Groups[i].Name))
            {
              text = text.Replace("${" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
              text = text.Replace("{" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
            }
          }
        }
      }

      return text;
    }

    private LinkedList<TriggerWrapper> GetActiveTriggers()
    {
      var activeTriggers = new LinkedList<TriggerWrapper>();
      var enabledTriggers = new List<Trigger>();

      lock (TriggerNodes)
      {
        LoadActiveTriggers(TriggerNodes, enabledTriggers);
      }

      var playerName = ConfigUtil.PlayerName;
      foreach (var trigger in enabledTriggers.OrderByDescending(trigger => trigger.LastTriggered))
      {
        if (trigger.Pattern is string pattern && !string.IsNullOrEmpty(pattern))
        {
          try
          {
            var wrapper = new TriggerWrapper
            {
              TriggerData = trigger,
              ModifiedSpeak = ModText(trigger.TextToSpeak),
              ModifiedWarningSpeak = ModText(trigger.WarningTextToSpeak),
              ModifiedEndSpeak = ModText(trigger.EndTextToSpeak),
              ModifiedEndEarlySpeak = ModText(trigger.EndEarlyTextToSpeak),
              ModifiedDisplay = ModText(trigger.TextToDisplay),
              ModifiedWarningDisplay = ModText(trigger.WarningTextToDisplay),
              ModifiedEndDisplay = ModText(trigger.EndTextToDisplay),
              ModifiedEndEarlyDisplay = ModText(trigger.EndEarlyTextToDisplay),
              ModifiedTimerName = ModText(string.IsNullOrEmpty(trigger.AltTimerName) ? trigger.Name : trigger.AltTimerName)
            };

            wrapper.ModifiedTimerName = string.IsNullOrEmpty(wrapper.ModifiedTimerName) ? "" : wrapper.ModifiedTimerName;
            wrapper.HasRepeated = wrapper.ModifiedTimerName.Contains("{repeated}", StringComparison.OrdinalIgnoreCase); 
            pattern = UpdatePattern(trigger.UseRegex, playerName, pattern, out List<NumberOptions> numberOptions);

            // temp
            if (wrapper.TriggerData.EnableTimer && wrapper.TriggerData.TimerType == 0)
            {
              wrapper.TriggerData.TimerType = 1;
            }

            if (trigger.UseRegex)
            {
              wrapper.Regex = new Regex(pattern, RegexOptions.IgnoreCase);
              wrapper.RegexNOptions = numberOptions;
            }
            else
            {
              wrapper.ModifiedPattern = pattern;
            }

            activeTriggers.AddLast(new LinkedListNode<TriggerWrapper>(wrapper));
          }
          catch (Exception ex)
          {
            LOG.Debug("Bad Trigger?", ex);
          }
        }
      }

      return activeTriggers;
    }

    private void LoadActiveTriggers(TriggerNode data, List<Trigger> triggers)
    {
      if (data != null && data.Nodes != null && data.IsEnabled != false)
      {
        foreach (var node in data.Nodes)
        {
          if (node.TriggerData != null)
          {
            triggers.Add(node.TriggerData);
          }
          else if (node.OverlayData == null)
          {
            LoadActiveTriggers(node, triggers);
          }
        }
      }
    }

    private string UpdatePattern(bool useRegex, string playerName, string pattern, out List<NumberOptions> numberOptions)
    {
      numberOptions = new List<NumberOptions>();
      pattern = pattern.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);

      if (useRegex)
      {
        if (Regex.Matches(pattern, @"{(s\d?)}", RegexOptions.IgnoreCase) is MatchCollection matches && matches.Count > 0)
        {
          foreach (Match match in matches)
          {
            if (match.Groups.Count == 2)
            {
              pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + ">.+)");
            }
          }
        }
        
        if (Regex.Matches(pattern, @"{(n\d?)(<=|>=|>|<|=|==)?(\d+)?}", RegexOptions.IgnoreCase) is MatchCollection matches2 && matches2.Count > 0)
        {
          foreach (Match match in matches2)
          {
            if (match.Groups.Count == 4)
            {
              pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + @">\d+)");

              if (!string.IsNullOrEmpty(match.Groups[2].Value) && !string.IsNullOrEmpty(match.Groups[3].Value) &&
                uint.TryParse(match.Groups[3].Value, out uint value))
              {
                numberOptions.Add(new NumberOptions { Key = match.Groups[1].Value, Op = match.Groups[2].Value, Value = value });
              }
            }
          }
        }
      }

      return pattern;
    }

    private void TriggerDataUpdated(object sender, EventArgs e)
    {
      TriggerUpdateTimer.Stop();

      if (TriggerUpdateTimer.Tag != null)
      {
        RequestRefresh();
        TriggerUpdateTimer.Tag = null;
      }

      SaveTriggers();
    }

    private void AddTextEvent(string text, Trigger data, MatchCollection matches, MatchCollection originalMatches = null)
    {
      if (!string.IsNullOrEmpty(text))
      {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
          text = ProcessText(text, originalMatches);
          text = ProcessText(text, matches);
          EventsAddText?.Invoke(this, new { Text = text, Trigger = data, CustomFont = data.FontColor });
        }, DispatcherPriority.Render);
      }
    }

    private void AddEntry(string line, Trigger trigger, string type, long eval = 0)
    {
      _ = Application.Current.Dispatcher.InvokeAsync(() =>
      {
        // update log
        var log = new ExpandoObject() as dynamic;
        log.Time = DateUtil.ToDouble(DateTime.Now);
        log.Line = line;
        log.Name = trigger.Name;
        log.Type = type;
        log.Eval = eval;
        log.Priority = trigger.Priority;
        log.Trigger = trigger;
        AlertLog.Insert(0, log);

        if (AlertLog.Count > 1000)
        {
          AlertLog.RemoveAt(AlertLog.Count - 1);
        }
      });
    }

    private void RequestRefresh()
    {
      if (RefreshTask == null || RefreshTask.IsCompleted)
      {
        RefreshTask = Task.Run(() =>
        {
          var updatedTriggers = GetActiveTriggers();
          lock (LockObject)
          {
            LogChannel?.Writer.WriteAsync(updatedTriggers);
          }
        });
      }
    }

    private void EventsLogLoadingComplete(object sender, bool e)
    {
      lock (LockObject)
      {
        if (LogChannel != null)
        {
          RequestRefresh();
        }
      }
    }

    private void SaveTriggers()
    {
      Application.Current?.Dispatcher.InvokeAsync(() =>
      {
        lock (TriggerNodes)
        {
          try
          {
            var json = JsonSerializer.Serialize(TriggerNodes, new JsonSerializerOptions { IncludeFields = true });
            ConfigUtil.WriteConfigFile(TRIGGERS_FILE, json);           
          }
          catch (Exception ex)
          {
            LOG.Error("Error Saving " + TRIGGERS_FILE, ex);
          }
        }
      });
    }

    private class Speak
    {
      public Trigger Trigger { get; set; }
      public string TTSOrSound { get; set; }
      public bool IsSound { get; set; }
      public MatchCollection Matches { get; set; }
      public MatchCollection OriginalMatches { get; set; }
    }

    private class LowPriData
    {
      public LinkedList<TriggerWrapper> ActiveTriggers { get; set; }
      public LinkedListNode<TriggerWrapper> Node { get; set; }
      public LineData LineData { get; set; }
      public Channel<Speak> SpeechChannel { get; set; }
    }

    private class TimerCount
    {
      public int Count { get; set; }
      public long CountTicks { get; set; }
    }

    private class TriggerWrapper
    {
      public List<TimerData> TimerList { get; set; } = new List<TimerData>();
      public string ModifiedPattern { get; set; }
      public string ModifiedSpeak { get; set; }
      public string ModifiedEndSpeak { get; set; }
      public string ModifiedEndEarlySpeak { get; set; }
      public string ModifiedWarningSpeak { get; set; }
      public string ModifiedDisplay { get; set; }
      public string ModifiedEndDisplay { get; set; }
      public string ModifiedEndEarlyDisplay { get; set; }
      public string ModifiedWarningDisplay { get; set; }
      public string ModifiedTimerName { get; set; }
      public Regex Regex { get; set; }
      public List<NumberOptions> RegexNOptions { get; set; }
      public Trigger TriggerData { get; set; }

      public Dictionary<string, TimerCount> TimerCounts = new Dictionary<string, TimerCount>();
      public bool HasRepeated { get; set; }
    }
  }
}
