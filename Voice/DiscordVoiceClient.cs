﻿using System;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp;

namespace DiscordUnity
{
    internal class DiscordVoiceClient : IDisposable
    {
        public bool isOnline { get; internal set; } = false;
        public DiscordChannel channel;
        public DiscordServer server;

        internal string token;
        internal string gateway;
        internal int port;
        internal uint ssrc;

        private DiscordClient parent;
        private UdpClient client;
        private IPEndPoint endpoint;
        private WebSocket socket;
        private Dictionary<DiscordUser, uint> users;
        private ushort sequence = 0;
        private uint timestamp = 0;
        private int heartbeat = 5500;
        private Thread heartbeatThread;
        private Thread udpThread;
        private Thread sendThread;
        private Thread receiveThread;
        private byte[] key;
        private string encryptionMode = "xsalsa20_poly1305";
        private Queue<byte[]> voiceToSend = new Queue<byte[]>();
        private OpusEncoder encoder;
        private OpusDecoder decoder;

        public DiscordVoiceClient(DiscordClient pclient, DiscordChannel pchannel)
        {
            parent = pclient;
            channel = pchannel;
            users = new Dictionary<DiscordUser, uint>();

            try
            {
                if (channel.ID != null && channel.type == DiscordChannelType.Voice)
                {
                    if (channel.bitrate > 0)
                    {
                        encoder = new OpusEncoder(48000, 1, 20, (channel.bitrate / 1000), OpusApplication.MusicOrMixed);
                    }

                    else
                    {
                        encoder = new OpusEncoder(48000, 1, 20, null, OpusApplication.MusicOrMixed);
                    }

                    encoder.SetForwardErrorCorrection(true);
                    decoder = new OpusDecoder(48000, 1, 20);
                }
            }

            catch (Exception e)
            {
                Debug.LogError(e.Message);
                Debug.LogError(e.StackTrace);
                Debug.LogError(e.Source);
            }
        }

        public void Start(DiscordServer pserver, string endpoint, string voiceToken)
        {
            server = pserver;
            token = voiceToken;
            gateway = endpoint;
            socket = new WebSocket("wss://" + gateway.Split(':')[0]);

            socket.OnMessage += (s, message) =>
            {
                try
                {
                    PayloadJSON e = JsonUtility.FromJson<PayloadJSON>(message.Data);
                    int payloadIndex = message.Data.IndexOf("\"d\":{");
                    string payload = message.Data.Substring(payloadIndex + 4, message.Data.Length - payloadIndex - 5);
                    Debugger.WriteLine("VOICE {" + e.op + "}: " + payload);
                    ProcessMessage(e.op, payload);
                }

                catch (Exception e)
                {
                    Debug.LogError("Received Error OnMessage: " + e.Message);
                    Debug.LogError("Received Error OnMessage: " + e.Source);
                    Debug.LogError("Received Error OnMessage: " + e.StackTrace);
                    Debugger.WriteLine("VOICE {ERROR}: " + message.Data);
                }
            };

            socket.OnOpen += (sender, e) =>
            {
                PayloadArgs<DiscordIdentifyArgs> args = new PayloadArgs<DiscordIdentifyArgs>()
                {
                    op = 0,
                    d = new DiscordIdentifyArgs()
                    {
                        server_id = server.ID,
                        user_id = parent.user.ID,
                        session_id = parent.sessionID,
                        token = token
                    }
                };

                socket.Send(JsonUtility.ToJson(args));
            };

            socket.OnError += (s, e) =>
            {
                Debug.LogError(e.Message);
            };

            socket.OnClose += (s, e) =>
            {
                if (!e.WasClean)
                {
                    Debug.LogError(e.Code);
                }

                Debug.LogWarning(e.Reason);
                isOnline = false;
                Dispose();
            };

            socket.Connect();
        }

        public void Dispose()
        {
            if (isOnline)
            {
                socket.Close();
                return;
            }

            client.Close();
            if(heartbeatThread.IsAlive) heartbeatThread.Abort();
            if (udpThread.IsAlive) udpThread.Abort();
            if (receiveThread.IsAlive) receiveThread.Abort();
            if (sendThread.IsAlive) sendThread.Abort();
            users.Clear();
            voiceToSend.Clear();
            heartbeatThread = null;
            udpThread = null;
            receiveThread = null;
            sendThread = null;
            socket = null;
            client = null;
            GC.SuppressFinalize(this);
            parent.unityInvoker.Enqueue(() => parent.OnVoiceStopped(this, new DiscordEventArgs() { client = parent }));
        }

        private bool _speaking;
        public bool speaking
        {
            get
            {
                return _speaking;
            }

            set
            {
                _speaking = value;
                SetSpeaking(value);
            }
        }

        private void SetSpeaking(bool speaking)
        {
            if (socket != null)
            {
                PayloadArgs<VoiceSpeakingArgs> args = new PayloadArgs<VoiceSpeakingArgs>()
                {
                    op = 5,
                    d = new VoiceSpeakingArgs()
                    {
                        speaking = speaking,
                        delay = 0
                    }
                };

                Debugger.WriteLine("Voice Send: " + JsonUtility.ToJson(args));
                socket.Send(JsonUtility.ToJson(args));

                if (!speaking)
                {
                    for (int x = 0; x < 5; x++)
                    {
                        voiceToSend.Enqueue(null);
                    }
                }
            }
        }
        public void ClearVoiceQueue()
        {
            voiceToSend.Clear();
        }

        private void ProcessMessage(int op, string payload)
        {
            switch (op)
            {
                case 2:
                    VoiceConnectionJSON connection = JsonUtility.FromJson<VoiceConnectionJSON>(payload);
                    users.Add(parent.user, connection.ssrc);

                    for (int i = 0; i < connection.modes.Length; i++)
                    {
                        if (!connection.modes[i].ToLower().Contains("plain"))
                        {
                            encryptionMode = connection.modes[i];
                            break;
                        }
                    }

                    ssrc = connection.ssrc;
                    port = connection.port;
                    heartbeat = connection.heartbeat_interval;
                    heartbeatThread = new Thread(KeepAlive);
                    heartbeatThread.Start();
                    ConnectUDP();
                    break;

                case 3:
                    // KeepAlive echo, ignore
                    break;

                case 4:
                    VoiceKeyJSON e = JsonUtility.FromJson<VoiceKeyJSON>(payload);
                    key = e.secret_key;

                    PayloadArgs<VoiceSpeakingArgs> args = new PayloadArgs<VoiceSpeakingArgs>()
                    {
                        op = 5,
                        d = new VoiceSpeakingArgs()
                        {
                            speaking = true,
                            delay = 0
                        }
                    };
                    
                    socket.Send(JsonUtility.ToJson(args));
                    udpThread = new Thread(UDPKeepAlive);
                    udpThread.Start();
                    isOnline = true;
                    parent.unityInvoker.Enqueue(() => parent.OnVoiceStarted(this, new DiscordEventArgs() { client = parent }));

                    sendThread = new Thread(SendVoiceLoop);
                    receiveThread = new Thread(ReceiveVoiceLoop);
                    sendThread.Start();
                    receiveThread.Start();

                    SetSpeaking(true);
                    break;

                case 5:
                    VoiceSpeakingJSON speaker = JsonUtility.FromJson<VoiceSpeakingJSON>(payload);
                    DiscordUser user = server._members[speaker.user_id].user;

                    if (!users.ContainsKey(user))
                    {
                        users.Add(user, speaker.ssrc);
                        parent.unityInvoker.Enqueue(() => parent.OnVoiceUserSpeaking(this, new DiscordUserSpeakingArgs() { client = parent, speaking = speaker.speaking, user = user }));
                    }

                    else
                    {
                        parent.unityInvoker.Enqueue(() => parent.OnVoiceUserSpeaking(this, new DiscordUserSpeakingArgs() { client = parent, speaking = speaker.speaking, user = user }));

                    }

                    break;
            }
        }

        private void SendVoiceLoop()
        {
            try
            {
                while (isOnline)
                {
                    if (voiceToSend.Count > 0)
                    {
                        SendVoice();
                    }

                    else
                    {
                        Thread.Sleep(1000);
                    }

                    if (sequence > 0 && timestamp > 0)
                    {
                        if (voiceToSend.Count == 0)
                        {
                            sequence = 0;
                            timestamp = 0;
                        }
                    }
                }
            }

            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }

        private void SendVoice()
        {
            byte[] buffer = voiceToSend.Dequeue();

            if (buffer != null)
            {
                System.Diagnostics.Stopwatch timeToSend = System.Diagnostics.Stopwatch.StartNew();

                byte[] packet = new byte[4028];
                byte[] nonce = new byte[24];

                packet[0] = (byte)0x80;
                packet[1] = (byte)0x78;

                packet[8] = (byte)(ssrc >> 24);
                packet[9] = (byte)(ssrc >> 16);
                packet[10] = (byte)(ssrc >> 8);
                packet[11] = (byte)(ssrc >> 0);

                sequence = unchecked(sequence++);
                packet[2] = (byte)(sequence >> 8);
                packet[3] = (byte)(sequence >> 0);

                packet[4] = (byte)(timestamp >> 24);
                packet[5] = (byte)(timestamp >> 16);
                packet[6] = (byte)(timestamp >> 8);
                packet[7] = (byte)(timestamp >> 0);
                
                byte[] opusAudio = new byte[buffer.Length];
                int encodedLength = encoder.EncodeFrame(buffer, 0, opusAudio);

                Buffer.BlockCopy(packet, 0, nonce, 0, 12);
                int returnVal = SecretBox.Encrypt(opusAudio, encodedLength, packet, nonce, key);
                if (returnVal != 0) return;
                if (opusAudio == null) Debug.LogError("opusAudio");
                
                int dataSent = client.Send(packet, encodedLength + 28);
                Debug.Log("Send: " + dataSent + " from " + buffer.Length);
                timestamp = unchecked(timestamp + (uint)encoder.SamplesPerFrame);
                parent.unityInvoker.Enqueue(() => parent.OnVoicePacketSend(this, new DiscordVoiceArgs() { channel = channel, client = parent, packet = packet, sender = parent.user }));

                timeToSend.Stop();
                Debug.Log("Time: " + timeToSend.ElapsedMilliseconds);

                if (timeToSend.ElapsedMilliseconds > 0)
                {
                    Debug.Log("Sleep for 16 ms!");
                    Thread.Sleep(20 - (int)timeToSend.ElapsedMilliseconds);
                }

                else
                {
                    Debug.Log("Sleep for 20 ms!");
                    Thread.Sleep(20);
                }
            }

            else
            {
                byte[] empty = new byte[3] { (byte)0xF8, (byte)0xFF, (byte)0xFE };
                client.Send(empty, empty.Length);
            }
        }

        private void ReceiveVoiceLoop()
        {
            while (isOnline)
            {
                try
                {
                    ReceiveVoice();
                }

                catch (Exception e)
                {
                    Debug.LogError("Error in receiving: " + e.Message);
                    Debug.LogError("Error in receiving: " + e.Source);
                    Debug.LogError("Error in receiving: " + e.StackTrace);
                }
            }
        }

        private void ReceiveVoice()
        {
            if (client.Available > 0)
            {
                byte[] packet, buffer = null, nonce = null, result;
                packet = client.Receive(ref endpoint);
                Debug.LogWarning("Received packet");
                int packetLength, resultLength;
                buffer = new byte[4000];
                nonce = new byte[24];
                packetLength = packet.Length;

                if (packet.Length > 0)
                {
                    if (packetLength < 12) return;
                    if (packet[0] != 0x80) return;
                    if (packet[1] != 0x78) return;

                    ushort sequenceNumber = (ushort)((packet[2] << 8) | packet[3] << 0);
                    uint timDocuestamp = (uint)((packet[4] << 24) | packet[5] << 16 | packet[6] << 8 | packet[7] << 0);
                    uint ssrc = (uint)((packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | (packet[11] << 0));

                    if (packetLength < 28) return;

                    Buffer.BlockCopy(packet, 0, nonce, 0, 12);
                    var length = Convert.ToUInt64(packetLength - 12);
                    int returnValue = SecretBox.Decrypt(packet, length, buffer, nonce, key);
                    if (returnValue != 0) return;
                    result = buffer;
                    resultLength = packetLength - 28;
                    //Debug.Log("rlen: " + length);

                    if (users.ContainsValue(ssrc))
                    {
                        byte[] output = new byte[4000];
                        // float[] outputFloat = new float[1000];
                        decoder.DecodeFrame(result, 0, resultLength, output);
                        float[] outputFloat = Utils.BytesToFloats(output);
                        //decoder.DecodeFrameFloat(result, 0, resultLength, outputFloat);
                        Debug.Log("Received: " + packet.Length + " for " + output.Length);
                        parent.unityInvoker.Enqueue(() => parent.OnVoicePacketReceived(this, new DiscordVoiceArgs()
                        { channel = channel, client = parent, packet = output, unitypacket = outputFloat, raw = result, sender = GetUserBySsrc(ssrc) }));
                    }

                }
            }
        }

        private DiscordUser GetUserBySsrc(uint ssrc)
        {
            foreach (var user in users)
            {
                if (user.Value == ssrc) return user.Key;
            }

            return null;
        }

        private void KeepAlive()
        {
            try
            {
                while (socket != null)
                {
                    Thread.Sleep(heartbeat);

                    PayloadArgs<long> args = new PayloadArgs<long>()
                    {
                        op = 3,
                        d = (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond
                    };

                    Debugger.WriteLine("VoiceBeat: " + JsonUtility.ToJson(args));
                    socket.Send(JsonUtility.ToJson(args));
                    Thread.Sleep(heartbeat);
                }
            }

            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }

        private void UDPKeepAlive()
        {

            byte[] keepAlive = new byte[5];
            keepAlive[0] = (byte)0xC9;

            try
            {
                while (socket != null)
                {
                    keepAlive[1] = (byte)((sequence >> 24) & 0xFF);
                    keepAlive[2] = (byte)((sequence >> 16) & 0xFF);
                    keepAlive[3] = (byte)((sequence >> 8) & 0xFF);
                    keepAlive[4] = (byte)((sequence >> 0) & 0xFF);
                    client.Send(keepAlive, keepAlive.Length);
                    Thread.Sleep(5000);
                }
            }

            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }

        private void ConnectUDP()
        {
            try
            {
                client = new UdpClient(port);
                client.DontFragment = false;
                client.Connect(gateway.Replace(":80", ""), port);
                endpoint = new IPEndPoint(Dns.GetHostAddresses(gateway.Replace(":80", ""))[0], 80);
                
                byte[] packet = new byte[70];
                packet[0] = (byte)((ssrc >> 24) & 0xFF);
                packet[1] = (byte)((ssrc >> 16) & 0xFF);
                packet[2] = (byte)((ssrc >> 8) & 0xFF);
                packet[3] = (byte)((ssrc >> 0) & 0xFF);
                client.Send(packet, packet.Length);
                byte[] returnBuffer = client.Receive(ref endpoint);

                if (returnBuffer != null && returnBuffer.Length > 0)
                {
                    int start = 4;
                    int end = 4;

                    for (int i = start; i < returnBuffer.Length; i++)
                    {
                        if (returnBuffer[i] != (byte)0) end++;
                        else break;
                    }

                    byte[] buffer = new byte[end - start];
                    Buffer.BlockCopy(returnBuffer, start, buffer, 0, buffer.Length);
                    IPAddress ip = IPAddress.Parse(System.Text.Encoding.ASCII.GetString(buffer));
                    int p = returnBuffer[returnBuffer.Length - 2] | returnBuffer[returnBuffer.Length - 1] << 8;
                    endpoint = new IPEndPoint(ip, p);

                    PayloadArgs<VoiceSendIPArgs> args = new PayloadArgs<VoiceSendIPArgs>()
                    {
                        op = 1,
                        d = new VoiceSendIPArgs()
                        {
                            protocol = "udp",
                            data = new VoiceSendIPDataArgs()
                            {
                                address = ip.ToString(),
                                port = p,
                                mode = encryptionMode
                            }
                        }
                    };

                    Debugger.WriteLine("Voice Send: " + JsonUtility.ToJson(args));
                    socket.Send(JsonUtility.ToJson(args));
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e.Message);
                Debug.LogError(e.StackTrace);
                Debug.LogError(e.Source);
            }
        }

        public void SendVoice(float[] voice)
        {
            SendVoice(Utils.FloatsToBytes(voice));
        }

        public void SendVoice(byte[] voice)
        {
            voiceToSend.Enqueue(voice);
        }
    }
}
