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
    private string? ipAddress;
    private int port;

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
            ipAddress = config.IP;
            port = config.Port;
        }
        else
        {
            
            Console.WriteLine("Error using config file. Using default values.");
            ipAddress = "0.0.0.0";
            port = 34001;
        }
    }

    private void StartUDPReceiver()
    {
        udpClient = new UdpClient(new IPEndPoint(IPAddress.Parse(ipAddress), port));
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
                        Console.WriteLine("UDP Recvied Data: " + audioByte);
                    }
                    else if (packetType == USRPVoicePacketType.Start)
                    {
                        Console.WriteLine("UDP Start Data");
                    }
                    else if (packetType == USRPVoicePacketType.End)
                    {
                        Console.WriteLine("UDP Stop Data");
                    }
                }
            }
            catch (SocketException ex)
            {
                Console.WriteLine("UDP Receiver Error: " + ex.Message);
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

        waveProvider.AddSamples(audioData, 0, audioData.Length);

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
        waveOut.Init(waveProvider);
        waveOut.Play();
    }

    private void StartWebSocketServer()
    {
        server = new WebSocketServer($"ws://{ipAddress}:8080");
        connectedSockets = new List<IWebSocketConnection>();

        server.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                Console.WriteLine("WebSocket connection open");
                connectedSockets.Add(socket);
            };

            socket.OnClose = () =>
            {
                Console.WriteLine("WebSocket connection closed");
                connectedSockets.Remove(socket);
            };

            socket.OnMessage = message =>
            {
                Console.WriteLine("Received WebSocket message: " + message);
            };

            socket.OnBinary = data =>
            {
                // Not implemented in this example
            };
        });
    }

    private void StartHttpListener()
    {
        httpListener = new HttpListener();
        httpListener.Prefixes.Add($"http://192.168.1.128:8081/"); // Set the URL for the web server. 0.0.0.0 prob wont work on windows

        try
        {
            httpListener.Start();
            isHttpListenerRunning = true;
            Console.WriteLine("Listening for HTTP requests...");

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
            Console.WriteLine("Error: " + ex.Message);
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
        public string? IP { get; set; }
        public int Port { get; set; }
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
