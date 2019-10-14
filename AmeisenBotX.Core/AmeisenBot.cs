﻿using AmeisenBotX.Core.Character;
using AmeisenBotX.Core.Data;
using AmeisenBotX.Core.Event;
using AmeisenBotX.Core.Hook;
using AmeisenBotX.Core.OffsetLists;
using AmeisenBotX.Core.StateMachine;
using AmeisenBotX.Core.StateMachine.CombatClasses;
using AmeisenBotX.Core.StateMachine.States;
using AmeisenBotX.Memory;
using AmeisenBotX.Memory.Win32;
using AmeisenBotX.Pathfinding;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Timers;
using Timer = System.Timers.Timer;

namespace AmeisenBotX.Core
{
    public class AmeisenBot
    {
        private double currentExecutionMs;
        private int stateMachineTimerBusy;

        public AmeisenBot(string botDataPath, string playername, AmeisenBotConfig config)
        {
            BotDataPath = botDataPath;
            PlayerName = playername;

            CurrentExecutionMs = 0;
            CurrentExecutionCount = 0;

            stateMachineTimerBusy = 0;

            StateMachineTimer = new Timer(config.StateMachineTickMs);
            StateMachineTimer.Elapsed += StateMachineTimerTick;

            Config = config;
            XMemory = new XMemory();
            OffsetList = new OffsetList335a();

            CacheManager = new CacheManager(BotDataPath, playername, config);
            ObjectManager = new ObjectManager(XMemory, OffsetList, CacheManager);
            CharacterManager = new CharacterManager(XMemory, OffsetList, ObjectManager);
            HookManager = new HookManager(XMemory, OffsetList, ObjectManager, CacheManager);
            EventHookManager = new EventHookManager(HookManager);
            PathfindingHandler = new NavmeshServerClient(Config.NavmeshServerIp, Config.NameshServerPort);

            if (!Directory.Exists(BotDataPath))
            {
                Directory.CreateDirectory(BotDataPath);
            }

            switch (Config.CombatClassName.ToUpper())
            {
                case "WARRIORARMS":
                    CombatClass = new WarriorArms(ObjectManager, CharacterManager, HookManager);
                    break;

                default:
                    CombatClass = null;
                    break;
            }

            StateMachine = new AmeisenBotStateMachine(BotDataPath, WowProcess, Config, XMemory, OffsetList, ObjectManager, CharacterManager, HookManager, EventHookManager, CacheManager, PathfindingHandler, CombatClass);

            StateMachine.OnStateMachineStateChange += HandlePositionLoad;
        }

        public string BotDataPath { get; }

        public string PlayerName { get; }

        public CacheManager CacheManager { get; set; }

        public CharacterManager CharacterManager { get; set; }

        public ICombatClass CombatClass { get; set; }

        public AmeisenBotConfig Config { get; }

        public EventHookManager EventHookManager { get; set; }

        public HookManager HookManager { get; set; }

        public ObjectManager ObjectManager { get; set; }

        public IOffsetList OffsetList { get; }

        public IPathfindingHandler PathfindingHandler { get; set; }

        public AmeisenBotStateMachine StateMachine { get; set; }

        public Process WowProcess { get; }

        public double CurrentExecutionMs
        {
            get
            {
                double avgTickTime = Math.Round(currentExecutionMs / CurrentExecutionCount, 2);
                CurrentExecutionCount = 0;
                return avgTickTime;
            }

            private set
            {
                currentExecutionMs = value;
            }
        }

        private int CurrentExecutionCount { get; set; }

        private Timer StateMachineTimer { get; }

        private XMemory XMemory { get; }

        public void Start()
        {
            StateMachineTimer.Start();
            CacheManager.LoadFromFile();

            EventHookManager.Subscribe("PARTY_INVITE_REQUEST", OnPartyInvitation);
            EventHookManager.Subscribe("RESURRECT_REQUEST", OnResurrectRequest);
            EventHookManager.Subscribe("CONFIRM_SUMMON", OnSummonRequest);
            EventHookManager.Subscribe("READY_CHECK", OnReadyCheck);
            //// EventHookManager.Subscribe("COMBAT_LOG_EVENT_UNFILTERED", OnCombatLog);
        }

        public void Stop()
        {
            StateMachineTimer.Stop();

            HookManager.DisposeHook();
            EventHookManager.Stop();

            if (ObjectManager.Player?.Name.Length > 0)
            {
                CacheManager.SaveToFile(ObjectManager.Player.Name);
                if (Config.SaveWowWindowPosition)
                {
                    SaveWowWindowPosition();
                }

                if (Config.SaveBotWindowPosition)
                {
                    SaveBotWindowPosition();
                }
            }
        }

        private void HandlePositionLoad()
        {
            if (StateMachine.CurrentState.Key == AmeisenBotState.Login)
            {
                if (Config.SaveWowWindowPosition)
                {
                    LoadWowWindowPosition();
                }

                if (Config.SaveBotWindowPosition)
                {
                    LoadBotWindowPosition();
                }
            }
        }

        private void LoadBotWindowPosition()
        {
            if (PlayerName.Length > 0)
            {
                string filepath = Path.Combine(BotDataPath, PlayerName, $"botpos.json");
                if (File.Exists(filepath))
                {
                    try
                    {
                        Rect rect = JsonConvert.DeserializeObject<Rect>(File.ReadAllText(filepath));
                        XMemory.SetWindowPosition(Process.GetCurrentProcess().MainWindowHandle, rect);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void LoadWowWindowPosition()
        {
            if (PlayerName.Length > 0)
            {
                string filepath = Path.Combine(BotDataPath, PlayerName, $"wowpos.json");
                if (File.Exists(filepath))
                {
                    try
                    {
                        Rect rect = JsonConvert.DeserializeObject<Rect>(File.ReadAllText(filepath));
                        XMemory.SetWindowPosition(XMemory.Process.MainWindowHandle, rect);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void SaveBotWindowPosition()
        {
            try
            {
                string filepath = Path.Combine(BotDataPath, PlayerName, $"botpos.json");
                Rect rect = XMemory.GetWindowPosition(Process.GetCurrentProcess().MainWindowHandle);
                File.WriteAllText(filepath, JsonConvert.SerializeObject(rect));
            }
            catch
            {
            }
        }

        private void SaveWowWindowPosition()
        {
            try
            {
                string filepath = Path.Combine(BotDataPath, PlayerName, $"wowpos.json");
                Rect rect = XMemory.GetWindowPosition(XMemory.Process.MainWindowHandle);
                File.WriteAllText(filepath, JsonConvert.SerializeObject(rect));
            }
            catch
            {
            }
        }

        private void StateMachineTimerTick(object sender, ElapsedEventArgs e)
        {
            // only start one timer tick at a time
            if (Interlocked.CompareExchange(ref stateMachineTimerBusy, 1, 0) == 1)
            {
                return;
            }

            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                StateMachine.Execute();
                CurrentExecutionMs = watch.ElapsedMilliseconds;
                CurrentExecutionCount++;
            }
            finally
            {
                stateMachineTimerBusy = 0;
            }
        }

        private void OnCombatLog(long timestamp, List<string> args)
        {
            // analyze the combat log
        }

        private void OnPartyInvitation(long timestamp, List<string> args)
            => HookManager.AcceptPartyInvite();

        private void OnReadyCheck(long timestamp, List<string> args)
            => HookManager.CofirmReadyCheck(true);

        private void OnResurrectRequest(long timestamp, List<string> args)
            => HookManager.AcceptResurrect();

        private void OnSummonRequest(long timestamp, List<string> args)
            => HookManager.AcceptSummon();
    }
}