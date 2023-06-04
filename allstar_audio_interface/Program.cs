//KO4UYJ Caleb
//Allstarlink Interface
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Fleck;
using NAudio.Wave;

public enum USRPVoicePacketType
{
    Start,
    Audio,
    End
}

public class UDPAudioStreamer
{
    private UdpClient? udpClient;
    private Thread? receiveThread;
    private BufferedWaveProvider? waveProvider;
    private WaveOutEvent? waveOut;
    private WebSocketServer? server;
    private List<IWebSocketConnection>? connectedSockets;
    private HttpListener? httpListener;
    private bool isHttpListenerRunning = false;
    private string? IP_USRP;
    private string? Socket_IP;
    private string? HTTP_IP;
    private int HTTP_PORT;
    private int Socket_PORT;
    private int USRP_PORT;
    private int DebugLevel;
    private int LocalSound;

    private void LogInfo(string message)
    {
        string formattedMessage = $"{DateTime.Now} [Info] {message}";
        Console.WriteLine(formattedMessage);
    }
    private void LogWarning(string message)
    {
        string formattedMessage = $"{DateTime.Now} [Warning] {message}";
        Console.WriteLine(formattedMessage);
    }
    private void LogError(string message)
    {
        string formattedMessage = $"{DateTime.Now} [Error] {message}";
        Console.WriteLine(formattedMessage);
    }

    public void Start()
    {
        LoadConfiguration();
        StartUDPReceiver();
        InitializeAudioOutput();
        StartWebSocketServer();
        StartHttpListener();
    }

    private void LoadConfiguration()
    {
        string configFilePath = "config.json";
        if (File.Exists(configFilePath))
        {
            string json = File.ReadAllText(configFilePath);
            var config = JsonConvert.DeserializeObject<Configuration>(json);
            IP_USRP = config.IP_USRP;
            Socket_IP = config.Socket_IP;
            HTTP_IP = config.HTTP_IP;
            HTTP_PORT = config.HTTP_PORT;
            Socket_PORT = config.Socket_PORT;
            USRP_PORT = config.USRP_PORT;
            DebugLevel = config.DebugLevel;
            LocalSound = config.LocalSound;


            LogInfo("Loaded Config:   " + configFilePath);
        }
        else
        {
            
            LogInfo("Error loading config file. Using default values.");
            IP_USRP = "0.0.0.0";
            USRP_PORT = 34001;
            Socket_IP = "0.0.0.0";
            HTTP_IP = "0.0.0.0";
            HTTP_PORT = 8081;
            Socket_PORT = 8080;
            DebugLevel = 0;
        }
    }

    private void StartUDPReceiver()
    {
        udpClient = new UdpClient(new IPEndPoint(IPAddress.Parse(IP_USRP), USRP_PORT));
        waveProvider = new BufferedWaveProvider(new WaveFormat(8000, 16, 1));

        receiveThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    IPEndPoint remoteEndPoint = null;
                    byte[] receivedData = udpClient.Receive(ref remoteEndPoint);

                    USRPVoicePacketType packetType = GetPacketType(receivedData);

                    if (packetType == USRPVoicePacketType.Audio)
                    {
                        ProcessAudioPacket(receivedData);
                        string audioByte = BitConverter.ToString(receivedData);
                        if (DebugLevel > 5)
                        {
                            LogInfo("UDP Recvied Data: " + audioByte);
                        }
                    }
                    else if (packetType == USRPVoicePacketType.Start)
                    {
                        if (DebugLevel > 2) {

                            LogInfo("UDP Start Data");
                        }
                    }
                    else if (packetType == USRPVoicePacketType.End)
                    {
                        if (DebugLevel > 2)
                        {
                            LogInfo("UDP Stop Data");
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                LogError("UDP Receiver Error: " + ex.Message);
            }
        });
        receiveThread.Start();
    }

    private USRPVoicePacketType GetPacketType(byte[] packet)
    {
        if (packet.Length >= 32)
        {
            uint packetTypeAsNum = BitConverter.ToUInt32(packet, 20);
            switch (packetTypeAsNum)
            {
                case 0:
                    if (packet.Length == 32)
                    {
                        return USRPVoicePacketType.End;
                    }
                    else
                    {
                        return USRPVoicePacketType.Audio;
                    }
                case 2:
                    return USRPVoicePacketType.Start;
            }
        }
        return USRPVoicePacketType.Audio; // Default to audio packet
    }

    private void ProcessAudioPacket(byte[] packet)
    {
        byte[] audioData = new byte[packet.Length - 32];
        Array.Copy(packet, 32, audioData, 0, audioData.Length);

        if (LocalSound == 1)
        {
            waveProvider.AddSamples(audioData, 0, audioData.Length);
        }

        // TODO: Actually make this work
        // Does not work
        string filePath = "audio.wav";
        using (var writer = new WaveFileWriter(filePath, waveProvider.WaveFormat))
        {
            writer.Write(audioData, 0, audioData.Length);
        }

       
        foreach (var socket in connectedSockets)
        {
            using (var fileStream = File.OpenRead(filePath))
            {
                byte[] fileData = new byte[fileStream.Length];
                fileStream.Read(fileData, 0, fileData.Length);
                socket.Send(fileData);
            }
        }
    }

    private void InitializeAudioOutput()
    {
        waveOut = new WaveOutEvent();

        if (LocalSound == 1)
        {
            waveOut.Init(waveProvider);
            waveOut.Play();
            LogInfo("Local speaker output enabled.");
        }
        else
        {
            LogInfo("Local speaker output disabled.");
        }
    }


    private void StartWebSocketServer()
    {
        server = new WebSocketServer($"ws://{Socket_IP}:{Socket_PORT}");
        connectedSockets = new List<IWebSocketConnection>();

        server.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                if (DebugLevel > 2)
                {
                    LogInfo("WebSocket connection open");
                }
                connectedSockets.Add(socket);

            };

            socket.OnClose = () =>
            {
                if (DebugLevel > 2)
                {
                    LogInfo("WebSocket connection closed");
                }
                connectedSockets.Remove(socket);

            };

            socket.OnMessage = message =>
            {
                if (DebugLevel > 2)
                {
                    LogInfo("Received WebSocket message: " + message);
                }
            };

            socket.OnBinary = data =>
            {
                // Not used for now
            };
        });
    }

    private void StartHttpListener()
    {
        httpListener = new HttpListener();
        httpListener.Prefixes.Add($"http://{HTTP_IP}:{HTTP_PORT}/");

        try
        {
            httpListener.Start();
            isHttpListenerRunning = true;
            LogInfo("HTTP Server Started");
            LogInfo("HTTP Info:  Host: " + HTTP_IP + " Port: " + HTTP_PORT);

            while (true)
            {
                var context = httpListener.GetContext();
                var request = context.Request;
                var response = context.Response;

                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/")
                {
                    // Serve the HTML content
                    string htmlContent = File.ReadAllText("index.html");
                    byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);

                    response.ContentType = "text/html";
                    response.ContentLength64 = buffer.Length;

                    response.OutputStream.Write(buffer, 0, buffer.Length);
                    response.OutputStream.Close();
                }
                else
                {
                    response.StatusCode = 404;
                    response.Close();
                }
            }
        }
        catch (HttpListenerException ex)
        {
            LogError("Error Starting HTTP Server: " + ex.Message);
        }
        finally
        {
            if (isHttpListenerRunning)
            {
                httpListener.Stop();
                isHttpListenerRunning = false;
            }
        }
    }

    public void Stop()
    {
        if (receiveThread != null)
        {
            receiveThread.Join();
            receiveThread = null;
        }

        if (waveOut != null)
        {
            waveOut.Stop();
            waveOut.Dispose();
            waveOut = null;
        }

        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }

        if (server != null)
        {
            server.Dispose();
            server = null;
        }

        if (httpListener != null && isHttpListenerRunning)
        {
            httpListener.Stop();
            httpListener.Close();
            isHttpListenerRunning = false;
        }
    }

    private class Configuration
    {
        public string? IP_USRP { get; set; }
        public string? HTTP_IP { get; set; }
        public string? Socket_IP { get; set; }

        public int USRP_PORT { get; set; }
        public int HTTP_PORT { get; set; }
        public int Socket_PORT { get; set; }
        public int DebugLevel { get; set; }
        public int LocalSound { get; set; }

    }
}

public class Program
{
    public static void Main()
    {
        UDPAudioStreamer audioStreamer = new UDPAudioStreamer();
        audioStreamer.Start();

        Console.WriteLine("Listening for audio packets. CTRL + C to stop...");
        Console.ReadKey();

        audioStreamer.Stop();
        Console.WriteLine("Audio streaming stopped. CTRL + C to exit...");
        Console.ReadKey();
    }
}
