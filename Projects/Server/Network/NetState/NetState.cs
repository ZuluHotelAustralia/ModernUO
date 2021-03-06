/*************************************************************************
 * ModernUO                                                              *
 * Copyright 2019-2020 - ModernUO Development Team                       *
 * Email: hi@modernuo.com                                                *
 * File: NetState.cs                                                     *
 *                                                                       *
 * This program is free software: you can redistribute it and/or modify  *
 * it under the terms of the GNU General Public License as published by  *
 * the Free Software Foundation, either version 3 of the License, or     *
 * (at your option) any later version.                                   *
 *                                                                       *
 * You should have received a copy of the GNU General Public License     *
 * along with this program.  If not, see <http://www.gnu.org/licenses/>. *
 *************************************************************************/

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Server.Accounting;
using Server.Gumps;
using Server.HuePickers;
using Server.Items;
using Server.Menus;

namespace Server.Network
{
    public delegate void NetStateCreatedCallback(NetState ns);

    public delegate void DecodePacket(CircularBuffer<byte> buffer, ref int length);
    public delegate void EncodePacket(ReadOnlySpan<byte> inputBuffer, CircularBuffer<byte> outputBuffer, out int length);

    public partial class NetState : IComparable<NetState>
    {
        private static int RecvPipeSize = 1024 * 64 + 1;
        private static int SendPipeSize = 1024 * 256 + 1;
        private static int GumpCap = 512;
        private static int HuePickerCap = 512;
        private static int MenuCap = 512;

        private static readonly Queue<NetState> FlushPending = new(2048);
        private static readonly ConcurrentQueue<NetState> Disposed = new();
        private static NetworkState NetworkState = NetworkState.ResumeState;

        public static NetStateCreatedCallback CreatedCallback { get; set; }

        private readonly string _toString;
        private ClientVersion _version;
        private long _nextActivityCheck;
        private volatile bool _running;
        private volatile DecodePacket _packetDecoder;
        private volatile EncodePacket _packetEncoder;
        private bool _flushQueued;

        internal int _authId;
        internal int _seed;

        public static void Configure()
        {
            RecvPipeSize = ServerConfiguration.GetOrUpdateSetting("netstate.recvPipeSize", RecvPipeSize);
            SendPipeSize = ServerConfiguration.GetOrUpdateSetting("netstate.sendPipeSize", SendPipeSize);
            GumpCap = ServerConfiguration.GetOrUpdateSetting("netstate.gumpCap", GumpCap);
            HuePickerCap = ServerConfiguration.GetOrUpdateSetting("netstate.huePickerCap", HuePickerCap);
            MenuCap = ServerConfiguration.GetOrUpdateSetting("netstate.menuCap", MenuCap);
        }

        public static void Initialize()
        {
            var checkAliveDuration = TimeSpan.FromMinutes(1.5);
            Timer.DelayCall(checkAliveDuration, checkAliveDuration, CheckAllAlive);
        }

        public NetState(ISocket connection)
        {
            _running = false;
            Connection = connection;
            Seeded = false;
            Gumps = new List<Gump>();
            HuePickers = new List<HuePicker>();
            Menus = new List<IMenu>();
            Trades = new List<SecureTrade>();
            var recvBuffer = GC.AllocateUninitializedArray<byte>(RecvPipeSize);
            RecvPipe = new Pipe<byte>(recvBuffer);
            var sendBuffer = GC.AllocateUninitializedArray<byte>(SendPipeSize);
            SendPipe = new Pipe<byte>(sendBuffer);
            _nextActivityCheck = Core.TickCount + 30000;

            try
            {
                Address = Utility.Intern((Connection?.RemoteEndPoint as IPEndPoint)?.Address);
                _toString = Address?.ToString() ?? "(error)";
            }
            catch (Exception ex)
            {
                TraceException(ex);
                Address = IPAddress.None;
                _toString = "(error)";
            }

            ConnectedOn = DateTime.UtcNow;

            CreatedCallback?.Invoke(this);
        }

        public DateTime ConnectedOn { get; }

        public TimeSpan ConnectedFor => DateTime.UtcNow - ConnectedOn;

        public DateTime ThrottledUntil { get; set; }

        public IPAddress Address { get; }

        public DecodePacket PacketDecoder
        {
            get => _packetDecoder;
            set => _packetDecoder = value;
        }

        public EncodePacket PacketEncoder
        {
            get => _packetEncoder;
            set => _packetEncoder = value;
        }

        public bool SentFirstPacket { get; set; }

        public bool BlockAllPackets { get; set; }

        public List<SecureTrade> Trades { get; }

        public bool Running => _running;

        public bool Seeded { get; set; }

        public Pipe<byte> RecvPipe { get; private set; }

        public Pipe<byte> SendPipe { get; private set; }

        public ISocket Connection { get; private set; }

        public bool CompressionEnabled { get; set; }

        public int Sequence { get; set; }

        public List<Gump> Gumps { get; private set; }

        public List<HuePicker> HuePickers { get; private set; }

        public List<IMenu> Menus { get; private set; }

        public CityInfo[] CityInfo { get; set; }

        public Mobile Mobile { get; set; }

        public ServerInfo[] ServerInfo { get; set; }

        public IAccount Account { get; set; }

        public int CompareTo(NetState other) => string.CompareOrdinal(_toString, other?._toString);

        public void ValidateAllTrades()
        {
            for (var i = Trades.Count - 1; i >= 0; --i)
            {
                if (i >= Trades.Count)
                {
                    continue;
                }

                var trade = Trades[i];

                if (trade.From.Mobile.Deleted || trade.To.Mobile.Deleted || !trade.From.Mobile.Alive ||
                    !trade.To.Mobile.Alive || !trade.From.Mobile.InRange(trade.To.Mobile, 2) ||
                    trade.From.Mobile.Map != trade.To.Mobile.Map)
                {
                    trade.Cancel();
                }
            }
        }

        public void CancelAllTrades()
        {
            for (var i = Trades.Count - 1; i >= 0; --i)
            {
                if (i < Trades.Count)
                {
                    Trades[i].Cancel();
                }
            }
        }

        public void RemoveTrade(SecureTrade trade)
        {
            Trades.Remove(trade);
        }

        public SecureTrade FindTrade(Mobile m)
        {
            for (var i = 0; i < Trades.Count; ++i)
            {
                var trade = Trades[i];

                if (trade.From.Mobile == m || trade.To.Mobile == m)
                {
                    return trade;
                }
            }

            return null;
        }

        public SecureTradeContainer FindTradeContainer(Mobile m)
        {
            for (var i = 0; i < Trades.Count; ++i)
            {
                var trade = Trades[i];

                var from = trade.From;
                var to = trade.To;

                if (from.Mobile == Mobile && to.Mobile == m)
                {
                    return from.Container;
                }

                if (from.Mobile == m && to.Mobile == Mobile)
                {
                    return to.Container;
                }
            }

            return null;
        }

        public SecureTradeContainer AddTrade(NetState state)
        {
            var newTrade = new SecureTrade(Mobile, state.Mobile);

            Trades.Add(newTrade);
            state.Trades.Add(newTrade);

            return newTrade.From.Container;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteConsole(string text)
        {
            Console.WriteLine("Client: {0}: {1}", this, text);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteConsole(string format, params object[] args)
        {
            WriteConsole(string.Format(format, args));
        }

        public void AddMenu(IMenu menu)
        {
            Menus ??= new List<IMenu>();

            if (Menus.Count < MenuCap)
            {
                Menus.Add(menu);
            }
            else
            {
                WriteConsole("Exceeded menu cap, disconnecting...");
                Disconnect();
            }
        }

        public void RemoveMenu(IMenu menu)
        {
            Menus?.Remove(menu);
        }

        public void RemoveMenu(int index)
        {
            Menus?.RemoveAt(index);
        }

        public void ClearMenus()
        {
            Menus?.Clear();
        }

        public void AddHuePicker(HuePicker huePicker)
        {
            HuePickers ??= new List<HuePicker>();

            if (HuePickers.Count < HuePickerCap)
            {
                HuePickers.Add(huePicker);
            }
            else
            {
                WriteConsole("Exceeded hue picker cap, disconnecting...");
                Disconnect();
            }
        }

        public void RemoveHuePicker(HuePicker huePicker)
        {
            HuePickers?.Remove(huePicker);
        }

        public void RemoveHuePicker(int index)
        {
            HuePickers?.RemoveAt(index);
        }

        public void ClearHuePickers()
        {
            HuePickers?.Clear();
        }

        public void AddGump(Gump gump)
        {
            Gumps ??= new List<Gump>();

            if (Gumps.Count < GumpCap)
            {
                Gumps.Add(gump);
            }
            else
            {
                WriteConsole("Exceeded gump cap, disconnecting...");
                Disconnect();
            }
        }

        public void RemoveGump(Gump gump)
        {
            Gumps?.Remove(gump);
        }

        public void RemoveGump(int index)
        {
            Gumps?.RemoveAt(index);
        }

        public void ClearGumps()
        {
            Gumps?.Clear();
        }

        public void LaunchBrowser(string url)
        {
            this.SendMessageLocalized(Serial.MinusOne, -1, MessageType.Label, 0x35, 3, 501231);
            this.SendLaunchBrowser(url);
        }

        public override string ToString() => _toString;

        public static void Pause()
        {
            NetworkState.Pause(ref NetworkState);
        }

        public static void Resume()
        {
            NetworkState.Resume(ref NetworkState);
        }

        public bool GetSendBuffer(out CircularBuffer<byte> cBuffer)
        {
            var result = SendPipe.Writer.TryGetMemory();
            cBuffer = new CircularBuffer<byte>(result.Buffer);

            return !(result.IsClosed || result.Length <= 0);
        }

        public void Send(ReadOnlySpan<byte> span)
        {
            if (span == null)
            {
                return;
            }

            var length = span.Length;
            if (Connection == null || BlockAllPackets || length <= 0 || !GetSendBuffer(out var buffer))
            {
                return;
            }

            try
            {
                if (_packetEncoder != null)
                {
                    _packetEncoder(span, buffer, out length);
                }
                else
                {
                    buffer.CopyFrom(span);
                }

                SendPipe.Writer.Advance((uint)length);

                if (!_flushQueued)
                {
                    FlushPending.Enqueue(this);
                    _flushQueued = true;
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex);
                TraceException(ex);
#endif
                Disconnect();
            }
        }

        public virtual void Send(Packet p)
        {
            if (Connection == null || BlockAllPackets)
            {
                p.OnSend();
                return;
            }

            var writer = SendPipe.Writer;

            try
            {
                byte[] buffer = p.Compile(CompressionEnabled, out var length);

                if (buffer.Length > 0 && length > 0)
                {
                    var result = writer.TryGetMemory();
                    if (result.IsClosed)
                    {
                        p.OnSend();
                        return;
                    }

                    if (result.Length >= length)
                    {
                        result.CopyFrom(buffer.AsSpan(0, length));
                        writer.Advance((uint)length);

                        if (!_flushQueued)
                        {
                            FlushPending.Enqueue(this);
                            _flushQueued = true;
                        }
                    }
                    else
                    {
                        WriteConsole("Too much data pending, disconnecting...");
                        Disconnect();
                    }
                }
                else
                {
                    WriteConsole("Didn't write anything!");
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex);
                TraceException(ex);
#endif
                Disconnect();
            }
            finally
            {
                p.OnSend();
            }
        }

        internal void Start()
        {
            if (Connection == null || _running)
            {
                return;
            }

            _running = true;

            ThreadPool.UnsafeQueueUserWorkItem(RecvTask, null);
            ThreadPool.UnsafeQueueUserWorkItem(SendTask, null);
        }

        private async void SendTask(object state)
        {
            var reader = SendPipe.Reader;

            try
            {
                while (_running)
                {
                    var result = await reader.Read();
                    if (result.IsClosed)
                    {
                        break;
                    }

                    if (result.Length <= 0)
                    {
                        continue;
                    }

                    var bytesWritten = await Connection.SendAsync(result.Buffer, SocketFlags.None).ConfigureAwait(false);

                    if (bytesWritten > 0)
                    {
                        _nextActivityCheck = Core.TickCount + 90000;
                        reader.Advance((uint)bytesWritten);
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex);
                TraceException(ex);
#endif
            }
            finally
            {
                Disconnect();
            }
        }

        private void DecodePacket(ArraySegment<byte>[] buffer, ref int length)
        {
            CircularBuffer<byte> cBuffer = new CircularBuffer<byte>(buffer);
            _packetDecoder?.Invoke(cBuffer, ref length);
        }

        private async void RecvTask(object state)
        {
            var socket = Connection;
            var writer = RecvPipe.Writer;

            try
            {
                while (_running)
                {
                    if (NetworkState == NetworkState.PauseState)
                    {
                        continue;
                    }

                    var result = await writer.GetMemory();

                    if (result.IsClosed)
                    {
                        break;
                    }

                    if (result.Length <= 0)
                    {
                        continue;
                    }

                    var bytesWritten = await socket.ReceiveAsync(result.Buffer, SocketFlags.None).ConfigureAwait(false);
                    if (bytesWritten <= 0)
                    {
                        break;
                    }

                    DecodePacket(result.Buffer, ref bytesWritten);

                    writer.Advance((uint)bytesWritten);
                    _nextActivityCheck = Core.TickCount + 90000;

                    // No need to flush
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex);
                TraceException(ex);
#endif
            }
            finally
            {
                Disconnect();
            }
        }

        public static int HandleAllReceives()
        {
            int count = 0;

            foreach (var ns in TcpServer.Instances)
            {
                if (ns.HandleReceive())
                {
                    count++;
                }
            }

            return count;
        }

        public bool HandleReceive()
        {
            if (Connection == null || !_running)
            {
                return false;
            }

            try
            {
                var reader = RecvPipe.Reader;

                // Process as many packets as we can synchronously
                while (true)
                {
                    var result = reader.TryRead();

                    if (result.IsClosed || result.Length <= 0)
                    {
                        return false;
                    }

                    var bytesProcessed = this.ProcessPacket(result.Buffer);

                    if (bytesProcessed <= 0)
                    {
                        // Error
                        // TODO: Throw exception instead?
                        if (bytesProcessed < 0)
                        {
                            Disconnect();
                            return false;
                        }

                        return true;
                    }

                    reader.Advance((uint)bytesProcessed);
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine(ex);
                TraceException(ex);
#endif
                Disconnect();
                return false;
            }
        }

        public void Flush()
        {
            if (Connection != null)
            {
                SendPipe.Writer.Flush();
            }

            _flushQueued = false;
        }

        public static int FlushAll()
        {
            int count = 0;
            while (FlushPending.TryDequeue(out var ns))
            {
                ns.Flush();
                count++;
            }

            while (Disposed.TryDequeue(out var ns))
            {
                ns.Dispose();
                TcpServer.Instances.Remove(ns);
                count++;
            }

            return count;
        }

        public void CheckAlive(long curTicks)
        {
            if (Connection != null && _nextActivityCheck - curTicks < 0)
            {
                WriteConsole("Disconnecting due to inactivity...");
                Disconnect();
            }
        }

        public static void CheckAllAlive()
        {
            try
            {
                long curTicks = Core.TickCount;

                foreach (var ns in TcpServer.Instances)
                {
                    ns.CheckAlive(curTicks);
                }
            }
            catch (Exception ex)
            {
                TraceException(ex);
            }
        }

        public bool CheckEncrypted(int packetID)
        {
            if (!SentFirstPacket && packetID != 0xF0 && packetID != 0xF1 && packetID != 0xCF && packetID != 0x80 &&
                packetID != 0x91 && packetID != 0xA4 && packetID != 0xEF)
            {
                WriteConsole("Encrypted client detected, disconnecting");
                Disconnect();
                return true;
            }

            return false;
        }

        public PacketHandler GetHandler(int packetID) => IncomingPackets.GetHandler(packetID);

        public static void TraceException(Exception ex)
        {
            try
            {
                using var op = new StreamWriter("network-errors.log", true);
                op.WriteLine("# {0}", DateTime.UtcNow);

                op.WriteLine(ex);

                op.WriteLine();
                op.WriteLine();
            }
            catch
            {
                // ignored
            }

            Console.WriteLine(ex);
        }

        public void Disconnect()
        {
            if (Connection == null || !_running)
            {
                return;
            }

            _running = false;

            try
            {
                Connection.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException ex)
            {
                TraceException(ex);
            }

            try
            {
                Connection.Close();
            }
            catch (SocketException ex)
            {
                TraceException(ex);
            }

            Disposed.Enqueue(this);
        }

        private void Dispose()
        {
            Connection = null;

            RecvPipe.Writer.Flush();
            SendPipe.Writer.Close();

            var m = Mobile;
            var a = Account;

            if (m != null)
            {
                m.NetState = null;
                Mobile = null;
            }

            Gumps.Clear();
            Menus.Clear();
            HuePickers.Clear();
            Account = null;
            ServerInfo = null;
            CityInfo = null;

            var count = TcpServer.Instances.Count;

            if (a != null)
            {
                WriteConsole("Disconnected. [{0} Online] [{1}]", count, a);
            }
            else
            {
                WriteConsole("Disconnected. [{0} Online]", count);
            }
        }
    }
}
