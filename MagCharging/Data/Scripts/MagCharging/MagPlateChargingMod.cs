using System.Collections.Generic;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode;

namespace MagPlatePowerMod
{
    // Lets a mobile grid charge from whatever grid it is locked to with a landing gear or
    // magnetic plate (both are IMyLandingGear), the same way a locked connector would.
    //
    // For each locked rover/base pair the mod adds:
    //  - on the base: battery-like sinks that request exactly what the rover's batteries request,
    //    so the base's power system feeds them with the same rules it uses for its own batteries
    //    (Auto batteries only take surplus from non-battery sources, Recharge batteries may also
    //    draw from other batteries);
    //  - on the rover: a power source that outputs exactly what the base delivered.
    // The rover's batteries then charge through their normal input, so their max input,
    // charge mode and recharge efficiency all apply.
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class MagPlatePowerSession : MySessionComponentBase
    {
        private const int UpdateInterval = 10; // ticks (60 ticks = 1 second)

        private static readonly MyStringHash BaseSinkGroup = MyStringHash.GetOrCompute("BatteryBlock");
        private static readonly MyStringHash BaseSourceGroup = MyStringHash.GetOrCompute("Battery");
        private static readonly MyStringHash RoverSourceGroup = MyStringHash.GetOrCompute("SolarPanels");

        // Battery-like sink/source pair hosted on a scene-less entity. Sharing a container is what
        // makes the distributor treat the pair as storage; the anchor block puts it on the base grid.
        private class Mirror
        {
            public MyEntity Host;
            public MyResourceSinkComponent Sink;
            public MyResourceSourceComponent Source;
            public float Demand;
        }

        private class Link
        {
            public KeyValuePair<long, long> Key;
            public IMyCubeGrid Rover;
            public IMyCubeGrid Base;
            public string Description;
            public MyResourceDistributorComponent BaseDistributor;
            public IMyCubeBlock Anchor;
            public Mirror Auto;
            public Mirror Recharge;
        }

        private struct Candidate
        {
            public IMyLandingGear Gear;
            public IMyCubeGrid Rover;
            public IMyCubeGrid Base;
        }

        private class Feed
        {
            public IMyCubeGrid Rover;
            public MyResourceDistributorComponent Distributor;
            public MyResourceSourceComponent Source;
            public float Output;
            public float AutoDemand;
            public float RechargeDemand;
            public int LinkCount;
        }

        private readonly HashSet<IMyLandingGear> _gears = new HashSet<IMyLandingGear>();
        private readonly HashSet<IMyCubeGrid> _grids = new HashSet<IMyCubeGrid>();
        private readonly Dictionary<KeyValuePair<long, long>, Link> _links = new Dictionary<KeyValuePair<long, long>, Link>();
        private readonly Dictionary<long, Feed> _feeds = new Dictionary<long, Feed>();

        private readonly Dictionary<KeyValuePair<long, long>, Candidate> _activePairs = new Dictionary<KeyValuePair<long, long>, Candidate>();
        private readonly List<IMyLandingGear> _deadGears = new List<IMyLandingGear>();
        private readonly List<Link> _linkBuffer = new List<Link>();
        private readonly List<IMyCubeGrid> _groupBuffer = new List<IMyCubeGrid>();

        private bool _active;
        private int _tick;

        public override void BeforeStart()
        {
            // Runs on clients too: like vanilla, every machine simulates power distribution,
            // while the server stays authoritative for stored battery charge.
            _active = true;
            MyAPIGateway.Entities.OnEntityAdd += OnEntityAdd;

            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities);
            foreach (var entity in entities)
                OnEntityAdd(entity);

            MyLog.Default.WriteLineAndConsole("[MagPlatePowerMod] Loaded, tracking " + _gears.Count + " landing gears/mag plates.");
        }

        // Top-level entities are grids; landing gears are blocks inside them.
        private void OnEntityAdd(IMyEntity entity)
        {
            var grid = entity as IMyCubeGrid;
            if (grid == null || !_grids.Add(grid))
                return;

            grid.OnBlockAdded += OnBlockAdded;
            grid.OnClose += OnGridClose;

            foreach (var gear in grid.GetFatBlocks<IMyLandingGear>())
                _gears.Add(gear);
        }

        private void OnBlockAdded(IMySlimBlock block)
        {
            var gear = block.FatBlock as IMyLandingGear;
            if (gear != null)
                _gears.Add(gear);
        }

        private void OnGridClose(IMyEntity entity)
        {
            var grid = (IMyCubeGrid)entity;
            grid.OnBlockAdded -= OnBlockAdded;
            grid.OnClose -= OnGridClose;
            _grids.Remove(grid);
        }

        public override void UpdateBeforeSimulation()
        {
            if (!_active || ++_tick < UpdateInterval)
                return;
            _tick = 0;

            try
            {
                FindActivePairs();
                SyncLinks();
                UpdateFlows();
            }
            catch (System.Exception ex)
            {
                MyLog.Default.WriteLineAndConsole("[MagPlatePowerMod] Update error: " + ex);
            }
        }

        private void FindActivePairs()
        {
            _activePairs.Clear();

            foreach (var gear in _gears)
            {
                if (gear.Closed || gear.MarkedForClose)
                {
                    _deadGears.Add(gear);
                    continue;
                }

                if (!gear.IsLocked || !gear.IsFunctional)
                    continue;

                IMyCubeGrid roverGrid = gear.CubeGrid;
                IMyCubeGrid baseGrid = GetGrid(gear.GetAttachedEntity());
                if (baseGrid == null || baseGrid == roverGrid)
                    continue;

                // A station gear locked onto a ship should charge the ship, not the station.
                if (roverGrid.IsStatic && !baseGrid.IsStatic)
                {
                    var swap = roverGrid;
                    roverGrid = baseGrid;
                    baseGrid = swap;
                }

                // Already sharing power (e.g. also docked by connector) - nothing to do.
                if (MyAPIGateway.GridGroups.HasConnection(roverGrid, baseGrid, GridLinkTypeEnum.Electrical))
                    continue;

                if (roverGrid.ResourceDistributor == null || baseGrid.ResourceDistributor == null)
                    continue;

                var key = new KeyValuePair<long, long>(roverGrid.EntityId, baseGrid.EntityId);
                var reverse = new KeyValuePair<long, long>(baseGrid.EntityId, roverGrid.EntityId);

                // Two ships gripping each other would otherwise feed each other in a loop.
                if (_activePairs.ContainsKey(reverse) || (_links.ContainsKey(reverse) && !_links.ContainsKey(key)))
                    continue;

                if (!_activePairs.ContainsKey(key))
                    _activePairs[key] = new Candidate { Gear = gear, Rover = roverGrid, Base = baseGrid };
            }

            foreach (var gear in _deadGears)
                _gears.Remove(gear);
            _deadGears.Clear();
        }

        private void SyncLinks()
        {
            // Drop links that are no longer locked, or whose grids now use a different power system.
            _linkBuffer.Clear();
            foreach (var link in _links.Values)
            {
                if (!_activePairs.ContainsKey(link.Key))
                {
                    _linkBuffer.Add(link);
                    MyLog.Default.WriteLineAndConsole("[MagPlatePowerMod] Unlocked: " + link.Description);
                }
                else if (link.BaseDistributor != link.Base.ResourceDistributor || GetFeed(link.Rover).Distributor != link.Rover.ResourceDistributor)
                {
                    _linkBuffer.Add(link);
                }
            }
            foreach (var link in _linkBuffer)
                DestroyLink(link);

            foreach (var pair in _activePairs)
            {
                if (_links.ContainsKey(pair.Key))
                    continue;

                var c = pair.Value;
                var link = CreateLink(pair.Key, c.Rover, c.Base, c.Gear);
                if (link != null)
                    MyLog.Default.WriteLineAndConsole("[MagPlatePowerMod] Locked: " + link.Description);
            }
        }

        private void UpdateFlows()
        {
            // Rover battery demand, split by charge mode. A rover locked to several bases
            // spreads its request across them.
            foreach (var feed in _feeds.Values)
            {
                feed.AutoDemand = 0f;
                feed.RechargeDemand = 0f;
                feed.Output = 0f;

                _groupBuffer.Clear();
                MyAPIGateway.GridGroups.GetGroup(feed.Rover, GridLinkTypeEnum.Electrical, _groupBuffer);
                foreach (var grid in _groupBuffer)
                {
                    foreach (var battery in grid.GetFatBlocks<IMyBatteryBlock>())
                    {
                        if (battery.ResourceSink == null)
                            continue;

                        float request = battery.ResourceSink.RequiredInputByType(MyResourceDistributorComponent.ElectricityId);
                        if (battery.ChargeMode == ChargeMode.Recharge)
                            feed.RechargeDemand += request;
                        else
                            feed.AutoDemand += request;
                    }
                }
            }

            foreach (var link in _links.Values)
            {
                // Keep the base-side sinks anchored to a block that is still on the base grid.
                if (link.Anchor.Closed || link.Anchor.CubeGrid != link.Base)
                {
                    var anchor = FindAnchor(link.Base);
                    if (anchor == null)
                        continue;

                    link.Anchor = anchor;
                    ReattachMirror(link, link.Auto);
                    ReattachMirror(link, link.Recharge);
                }

                var feed = GetFeed(link.Rover);
                feed.Output += link.Auto.Sink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId)
                             + link.Recharge.Sink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId);

                SetDemand(link.Auto, feed.AutoDemand / feed.LinkCount);
                SetDemand(link.Recharge, feed.RechargeDemand / feed.LinkCount);
            }

            // The rover may only use what the base actually delivered.
            foreach (var feed in _feeds.Values)
                feed.Source.SetMaxOutputByType(MyResourceDistributorComponent.ElectricityId, feed.Output);
        }

        private static void SetDemand(Mirror mirror, float demand)
        {
            mirror.Demand = demand;
            mirror.Sink.SetMaxRequiredInputByType(MyResourceDistributorComponent.ElectricityId, demand);
            mirror.Sink.Update();
        }

        private Link CreateLink(KeyValuePair<long, long> key, IMyCubeGrid roverGrid, IMyCubeGrid baseGrid, IMyLandingGear gear)
        {
            var anchor = FindAnchor(baseGrid);
            if (anchor == null)
                return null;

            Feed feed;
            if (!_feeds.TryGetValue(roverGrid.EntityId, out feed))
            {
                feed = CreateFeed(roverGrid);
                if (feed == null)
                    return null;
                _feeds[roverGrid.EntityId] = feed;
            }

            var link = new Link
            {
                Key = key,
                Rover = roverGrid,
                Base = baseGrid,
                Description = "'" + roverGrid.DisplayName + "' <- '" + baseGrid.DisplayName + "' via " + gear.CustomName,
                BaseDistributor = (MyResourceDistributorComponent)baseGrid.ResourceDistributor,
                Anchor = anchor,
            };
            link.Auto = CreateMirror(link, false);
            link.Recharge = CreateMirror(link, true);

            feed.LinkCount++;
            _links[key] = link;
            return link;
        }

        private void DestroyLink(Link link)
        {
            RemoveMirror(link, link.Auto);
            RemoveMirror(link, link.Recharge);
            _links.Remove(link.Key);

            Feed feed;
            if (_feeds.TryGetValue(link.Rover.EntityId, out feed) && --feed.LinkCount <= 0)
            {
                feed.Distributor.RemoveSource(feed.Source);
                feed.Rover.Components.Remove<MyResourceSourceComponent>();
                _feeds.Remove(link.Rover.EntityId);
            }
        }

        private static Feed CreateFeed(IMyCubeGrid roverGrid)
        {
            // Hosted on the grid itself so the distributor files it under the rover's electrical group.
            if (roverGrid.Components.Has<MyResourceSourceComponent>())
            {
                MyLog.Default.WriteLineAndConsole("[MagPlatePowerMod] '" + roverGrid.DisplayName + "' already has a grid-level power source; not linking.");
                return null;
            }

            var source = new MyResourceSourceComponent();
            source.Init(RoverSourceGroup, new MyResourceSourceInfo
            {
                ResourceTypeId = MyResourceDistributorComponent.ElectricityId,
                DefinedOutput = 0f,
                ProductionToCapacityMultiplier = 1f,
                IsInfiniteCapacity = true,
            });
            roverGrid.Components.Add(source);
            // MaxOutput is capped by remaining capacity, which starts at 0.
            source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, float.PositiveInfinity);
            source.Enabled = true;
            source.SetProductionEnabledByType(MyResourceDistributorComponent.ElectricityId, true);
            source.SetMaxOutputByType(MyResourceDistributorComponent.ElectricityId, 0f);

            var distributor = (MyResourceDistributorComponent)roverGrid.ResourceDistributor;
            distributor.AddSource(source);

            return new Feed { Rover = roverGrid, Distributor = distributor, Source = source };
        }

        // stockpile = behave like a battery in Recharge mode (may draw from other batteries);
        // otherwise like Auto (only surplus from non-battery sources).
        private static Mirror CreateMirror(Link link, bool stockpile)
        {
            var mirror = new Mirror { Host = new MyEntity() };

            mirror.Source = new MyResourceSourceComponent();
            mirror.Source.Init(BaseSourceGroup, new MyResourceSourceInfo
            {
                ResourceTypeId = MyResourceDistributorComponent.ElectricityId,
                DefinedOutput = 0f,
                ProductionToCapacityMultiplier = 1f,
                IsInfiniteCapacity = false,
            });
            mirror.Source.SetProductionEnabledByType(MyResourceDistributorComponent.ElectricityId, !stockpile);

            mirror.Sink = new MyResourceSinkComponent();
            mirror.Sink.Init(BaseSinkGroup, new MyResourceSinkInfo
            {
                ResourceTypeId = MyResourceDistributorComponent.ElectricityId,
                MaxRequiredInput = 0f,
                RequiredInputFunc = () => mirror.Demand,
            });

            mirror.Host.Components.Add(mirror.Source);
            mirror.Host.Components.Add(mirror.Sink);
            mirror.Sink.TemporaryConnectedEntity = link.Anchor;

            link.BaseDistributor.AddSource(mirror.Source);
            link.BaseDistributor.AddSink(mirror.Sink);
            return mirror;
        }

        private static void RemoveMirror(Link link, Mirror mirror)
        {
            link.BaseDistributor.RemoveSink(mirror.Sink);
            link.BaseDistributor.RemoveSource(mirror.Source);
        }

        // Re-register so the distributor regroups the sink under its new anchor's grid.
        private static void ReattachMirror(Link link, Mirror mirror)
        {
            RemoveMirror(link, mirror);
            mirror.Sink.TemporaryConnectedEntity = link.Anchor;
            link.BaseDistributor.AddSource(mirror.Source);
            link.BaseDistributor.AddSink(mirror.Sink);
        }

        private Feed GetFeed(IMyCubeGrid rover)
        {
            return _feeds[rover.EntityId];
        }

        private static IMyCubeBlock FindAnchor(IMyCubeGrid grid)
        {
            foreach (var block in grid.GetFatBlocks<IMyCubeBlock>())
            {
                if (!block.Closed && !block.MarkedForClose)
                    return block;
            }
            return null;
        }

        private static IMyCubeGrid GetGrid(IMyEntity entity)
        {
            while (entity != null)
            {
                var grid = entity as IMyCubeGrid;
                if (grid != null)
                    return grid;
                entity = entity.Parent;
            }
            return null;
        }

        protected override void UnloadData()
        {
            if (_active && MyAPIGateway.Entities != null)
                MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdd;

            _linkBuffer.Clear();
            _linkBuffer.AddRange(_links.Values);
            foreach (var link in _linkBuffer)
                DestroyLink(link);

            foreach (var grid in _grids)
            {
                grid.OnBlockAdded -= OnBlockAdded;
                grid.OnClose -= OnGridClose;
            }

            _grids.Clear();
            _gears.Clear();
            _active = false;
        }
    }
}
