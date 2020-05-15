﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace SysDVRClient
{
	class Program
	{
		static IMutliStreamManager ParseLegacyArgs(string[] args)
		{
			IOutTarget VTarget = null, ATarget = null;

			void ParseTargetArgs(int baseIndex, ref IOutTarget t)
			{
				switch (args[baseIndex + 1])
				{
					case "mpv":
					case "stdin":
						{
							string fargs = "";
							if (args[baseIndex + 1] == "mpv")
								fargs = args[baseIndex] == "video" ? "- --profile=low-latency --no-cache --cache-secs=0 --demuxer-readahead-secs=0 --untimed --cache-pause=no --no-correct-pts --fps=30" : "- --no-video --demuxer=rawaudio --demuxer-rawaudio-rate=48000 ";

							if (args.Length > baseIndex + 4 && args[baseIndex + 3] == "args")
								fargs += args[baseIndex + 4];

							t = new StdInTarget(args[baseIndex + 2], fargs);

							break;
						}
					case "tcp":
						{
							int port = int.Parse(args[baseIndex + 2]);
							Console.WriteLine($"Waiting for a client to connect on {port} ...");
							t = new TCPTarget(System.Net.IPAddress.Any, port);
							break;
						}
					case "file":
						t = new OutFileTarget(args[baseIndex + 2]);
						break;
					default:
						throw new Exception($"{args[baseIndex + 1]} is not a valid video mode");
				}
			}

			int index = Array.IndexOf(args, "video");
			if (index >= 0) ParseTargetArgs(index, ref VTarget);
			index = Array.IndexOf(args, "audio");
			if (index >= 0) ParseTargetArgs(index, ref ATarget);

			return new LegacyUsbStreamManager(VTarget, ATarget);
		}

		static void PrintGuide(bool full)
		{
			if (!full) {
				Console.WriteLine("Basic usage:\r\n" +
						"Simply launching this exectuable will show this message and launch the RTSP server via USB.\r\n" +
						"Use 'SysDVR-Client rtsp' to stream directly, add '--no-audio' or '--no-video' to disable one of the streams\r\n" +
						"To stream in TCP Bridge mode launch 'SysDVR-Client bridge <switch ip address>'\r\n" +
						"Command line options for the previous version are still available, you can view them with 'SysDVR-Client --help'\r\n" +
						"Press enter to continue.\r\n");
				Console.ReadLine();
				return;
			}
			//TODO: completely rewrite this
			Console.WriteLine("Usage: \r\n" +
					"Stream via RTSP: 'SysDVR-Client rtsp', add '--no-audio' or '--no-video' to disable one of the streams\r\n" +
					"Stream via TCP Bridge: 'SysDVR-Client bridge <switch ip address>', '--no-audio' or '--no-video' are supported here too" +
					"Raw streaming options:\r\n" +
					"'SysDVR-Client video <stream config for video> audio <stream config for audio>'\r\n" +
					"You can omit the stream you don't want\r\n" +
					"Stream config is one of the following:\r\n" +
					" - tcp <port> : stream the data over a the network on the specified port.\r\n" +
					" - file <file name> : stores the received data to a file\r\n" +
					"   The format is raw h264 data for video and uncompressed s16le stereo 48kHz samples for sound\r\n" +
					" - stdin <executable path> args <program arguments> : Pipes the received data to another program, <executable path> is the other program's path and <program arguments> are the args to pass to the target program, you can omit args if the program doesn't need any configuration\r\n" +
					" - mpv <mpv player path> : same as stdin but automatically configures args for mpv. On windows use mpv.com instead of mpv.exe, omitting the extension will automatically use the right one\r\n" +
					"Streaming both video and audio at the same time could cause performance issues.\r\n" +
					"Note that tcp mode will wait until a program connects\r\n\r\n" +
					"Example commands: \r\n" +
					"SysDVR-Client audio mpv C:/programs/mpv/mpv : Plays audio via mpv located at C:/programs/mpv/mpv, video is ignored\r\n" +
					"SysDVR-Client video mpv ./mpv audio mpv ./mpv : Plays video and audio via mpv (path has to be specified twice)\r\n" +
					"SysDVR-Client video mpv ./mpv args \"--cache=no --cache-secs=0\" : Plays video in mpv disabling cache, audio is ignored\r\n" +
					"SysDVR-Client video tcp 1337 audio file C:/audio.raw : Streams video over port 1337 while saving audio to disk\r\n\r\n" +
					"Opening raw files in mpv: \r\n" +
					"mpv videofile.264 --no-correct-pts --fps=30 --cache=no --cache-secs=0\r\n" +
					"mpv audiofile.raw --no-video --demuxer=rawaudio --demuxer-rawaudio-rate=48000\r\n" +
					"(you can also use tcp://localhost:<port> instead of the file name to open the tcp stream)\r\n\r\n" +
					"Info to keep in mind:\r\n" +
					"Streaming works only with games that have game recording enabled.\r\n" +
					"If the video is very delayed or lagging try going to the home menu for a few seconds to force it to re-synchronize.\r\n" +
					"After disconnecting and reconnecting the usb wire the stream may not start right back, go to the home menu for a few seconds to let the sysmodule drop the last usb packets.\r\n\r\n" +
					"Experimental/Debug options:\r\n" +
					"--print-stats : print the average transfer speed and loop count for each thread every second\r\n" +
					"--usb-warn : print warnings from libusb\r\n" +
					"--usb-debug : print verbose output from libusb");
		}

		static void Main(string[] args)
		{
			Console.WriteLine("SysDVR-Client - 3.0 by exelix");
			Console.WriteLine("https://github.com/exelix11/SysDVR \r\n");
			if (args.Length < 1)
				PrintGuide(false);
			else if (args[0].Contains("help"))
			{
				PrintGuide(true);
				return;
			}

			IMutliStreamManager Streams = null;
			bool NoAudio = false, NoVideo = false;
			int Port;

			bool HasArg(string arg) => Array.IndexOf(args, arg) != -1;
			string ArgValue(string arg) 
			{
				int index = Array.IndexOf(args, arg);
				if (index == -1) return null;
				if (args.Length <= index + 1) return null;
				return args[index + 1];
			}

			int? ArgValueInt(string arg) 
			{
				var a = ArgValue(arg);
				if (int.TryParse(a, out int res))
					return res;
				return null;
			}

			if (HasArg("--usb-warn")) UsbHelper.LogLevel = LogLevel.Info;
			if (HasArg("--usb-debug")) UsbHelper.LogLevel = LogLevel.Debug;

			NoAudio = HasArg("--no-audio");
			NoVideo = HasArg("--no-video");
			Port = ArgValueInt("--port") ?? 6666;
			StreamingThread.Logging = HasArg("--print-stats");
			UsbHelper.ForceLibUsb = HasArg("--no-winusb");

			if (Port <= 1024)
				Console.WriteLine("Warning: ports lower than 1024 are usually reserved and may require administrator/root privileges");

			if (NoVideo && NoAudio)
			{
				Console.WriteLine("Specify at least a video or audio target");
				return;
			}

			if (args.Length == 0 || args[0].ToLower() == "rtsp")
				Streams = new UsbStreamManager(!NoVideo, !NoAudio, Port);
			else if (args[0].ToLower() == "bridge")
				Streams = new TCPBridgeManager(!NoVideo, !NoAudio, args[1], Port);
			else
				Streams = ParseLegacyArgs(args);

			StartStreaming(Streams, args);
		}

		static void StartStreaming(IMutliStreamManager Streams, string[] args)
		{
			Console.WriteLine("Starting stream, press return to stop");
			Console.WriteLine("If the stream lags try pausing and unpausing the player.");
			Streams.Begin();

			Console.ReadLine();
			Console.WriteLine("Terminating threads...");

			Streams.Stop();
		}
	}
}
