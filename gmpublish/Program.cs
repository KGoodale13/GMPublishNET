using GMPublish.LZMA;
using Ionic.Zip;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Unified.Internal;
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using GMPublish.GMAD;

namespace GMPublish
{
    class Program
    {
        static SteamClient steamClient;
        static CallbackManager manager;

        static SteamUser steamUser;

        static SteamUnifiedMessages steamUnifiedMessages;

        static bool isRunning;

        // Authentication shit
        private static string user, pass;
        //static string authCode, twoFactorAuth;
        static Action currentAction;

        // The type of actions i.e gmpublish create -addon some.gma -icon i.jpg => CREATE command
        enum Action { CREATE, UPDATE, LIST };

        // Runtime variables set by command arguments or environment variables
        // Some are only set for certain command types

        // FileInfo for commands using the -addon and/or -icon arguments
        static FileInfo gmaFile, iconFile;

        // For updates this is the id of the existing workshop item
        static int workshopId;

      

        public static readonly uint APPID = 4000;

        public static void Main(string[] args)
        {

            if (args.Length < 1)
            {
                Console.WriteLine("GMPublish: You need to pass at least one agument!");
                return;
            }

            // Get the user and pass from the args if they are set. 
            // The can be passed via args or via environment variables
            user = FindArgumentValue("-user", args, false);
            pass = FindArgumentValue("-pass", args, false);

            if(user == null)
                user = Environment.GetEnvironmentVariable("GMPUBLISH_USER");

            if (pass == null)
                pass = Environment.GetEnvironmentVariable("GMPUBLISH_PASS");

            if(user == null || pass == null){
                Console.WriteLine("GMPublish: You must pass a username and password via the -user and -pass arguments OR via the GMPUBLISH_USER and GMPUBLISH_PASS environment variables.");
                Exit(8);
                return;
            }

            string gmaFilePath, iconFilePath;
            switch (args[0]) {
                case "create":
                    currentAction = Action.CREATE;

                    gmaFilePath = FindArgumentValue("-addon", args, true);
                    gmaFile = GetFileInfoOrExit(gmaFilePath);

                    iconFilePath = FindArgumentValue("-icon", args, true);
                    iconFile = GetFileInfoOrExit(iconFilePath);
                    break;
                case "update":
                    currentAction = Action.UPDATE;

                    gmaFilePath = FindArgumentValue("-addon", args, true);
                    gmaFile = GetFileInfoOrExit(gmaFilePath);

                    workshopId = int.Parse(FindArgumentValue("-id", args, true));
                   
                    iconFilePath = FindArgumentValue("-icon", args, false);
                    if(iconFilePath != null)
                        iconFile = GetFileInfoOrExit(iconFilePath);
                    break;
                case "list":
                    currentAction = Action.LIST;
                    break;
                default:
                    Console.WriteLine("Invalid command please use either create, update or list");
                    Exit(8);
                    break;

            }

            SteamDirectory.Initialize().Wait();

            steamClient = new SteamClient();
            manager = new CallbackManager(steamClient);
            steamUser = steamClient.GetHandler<SteamUser>();
            steamUnifiedMessages = steamClient.GetHandler<SteamUnifiedMessages>();

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

            Console.WriteLine("Connecting to Steam...");
            isRunning = true;
            steamClient.Connect();
            while (isRunning)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
            Console.WriteLine("Done?");
            Console.ReadLine();
        }

        // Searches through the arguments for the argName and then subsequently looks for a proceeding value. 
        // GMPublish only accepts arguments in the format -arg <space> <value> so that is all this will support. No -arg=vaue or -argvalue
        // if required is set we will exit if the value is not found
        static string FindArgumentValue(string argName, string[] args, bool required = false){
            // Skip the first argument because that is the verb and itterate over the rest in pairs
            for (int i = 1; i < args.Length - 1; i++){
                if(args[i] == argName){
                    return args[i + 1];
                }
            }
            if (required){
                Console.WriteLine("Error: argument " + argName + " is required!");
                Exit(8);
            }
            return null;
        }

        static FileInfo GetFileInfoOrExit(string path){
            var fileinfo = new FileInfo(path);
            if (!fileinfo.Exists){
                Console.WriteLine("Error: File " + path + " not found!");
                Exit(9);
            }
            return fileinfo;
        }

        static void Exit(int code){System.Environment.Exit(code);}

        struct AddonInfo
        {
            public string title;
            public DescriptionJSON description;
        }

        static AddonInfo GetAddonInfoFromGMAFile(Stream gmaStream){
            AddonInfo addonInfo;
            using (BinaryReader binaryReader = new BinaryReader(gmaStream))
            using (gmaStream){
                // Skip past the first part of the gma header to get to the title and description the first entry sizes are 4, 1, 8, 8, 1, then title and description
                gmaStream.Seek(22, SeekOrigin.Begin);

                addonInfo.title = binaryReader.ReadNullTerminatedString();
                addonInfo.description = JsonConvert.DeserializeObject<DescriptionJSON>(binaryReader.ReadNullTerminatedString());
            }
            return addonInfo;
        }

        static byte[] SHAHash(Stream stream)
        {

            using (var sha = new SHA1Managed())
            {
                byte[] hash;
                hash = sha.ComputeHash(stream);
                stream.Seek(0, SeekOrigin.Begin);
                return hash;
            }
        }

        static async Task UploadIcon(Stream iconStream){
            var hash = SHAHash(iconStream);
            var iconSuccess = await CloudStream.UploadStream("gmpublish_icon.jpg", APPID, hash, iconStream.Length, steamClient, iconStream);
            if (!iconSuccess) { 
                Console.WriteLine("JPG Upload failed");
                Exit(32);
            }
        }

        static async Task UploadAddonGMA(Stream gmaStream)
        {
            var lzmaStream = LZMAEncodeStream.CompressStreamLZMA(gmaStream);
            var hashGma = SHAHash(lzmaStream);
            var gmaSuccess = await CloudStream.UploadStream("gmpublish.gma", APPID, hashGma, lzmaStream.Length, steamClient, lzmaStream);
            if (!gmaSuccess) { 
                Console.WriteLine("GMA Upload failed"); 
                Exit(32);
            }
        }

        static void FullyLoggedIn(SteamUser.LoggedOnCallback callback)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    if(currentAction == Action.CREATE || currentAction == Action.UPDATE) {
                        // Delete any previously uploaded temp files
                        await CloudStream.DeleteFile("gmpublish_icon.jpg", APPID, steamClient);
                        await CloudStream.DeleteFile("gmpublish.gma", APPID, steamClient);
                        AddonInfo addonInfo;

                        addonInfo = GetAddonInfoFromGMAFile(gmaFile.OpenRead());

                        using (Stream gmaStream = gmaFile.OpenRead())
                        {
                            // for updates the icon file is optional
                            if(iconFile != null && iconFile.Exists){
                                using (Stream iconStream = iconFile.OpenRead())
                                {
                                    await UploadIcon(iconStream);
                                }
                            }

                            await UploadAddonGMA(gmaStream);
                        }

                        var publishService = steamUnifiedMessages.CreateService<IPublishedFile>();

                        if (currentAction == Action.CREATE)
                        {
                            Console.WriteLine("Creating new addon - " + addonInfo.title);

                            var request = new CPublishedFile_Publish_Request
                            {
                                appid = APPID,
                                consumer_appid = APPID,
                                cloudfilename = "gmpublish.gma",
                                preview_cloudfilename = "gmpublish_icon.jpg",
                                title = addonInfo.title,
                                file_description = addonInfo.description.Description,
                                file_type = (uint)EWorkshopFileType.Community,
                                visibility = (uint)EPublishedFileVisibility.Public,
                                collection_type = addonInfo.description.Type,
                            };
                            foreach (var tag in addonInfo.description.Tags) { request.tags.Add(tag); }

                            var publishCallback = await publishService.SendMessage(publish => publish.Publish(request));
                            var publishResponse = publishCallback.GetDeserializedResponse<CPublishedFile_Publish_Response>();
                            var newId = publishResponse.publishedfileid;
                            Console.WriteLine(publishResponse.redirect_uri);
                            Console.WriteLine("Success! New Addon Published. Addon ID: " + newId.ToString());
                            Exit(1); // 1 signals success in the original gmpublish
                        }
                        else // currentAction == Action.UPDATE
                        {
                            Console.WriteLine("Updating existing addon " + workshopId + " - " + addonInfo.title);
                            var request = new CPublishedFile_Update_Request
                            {
                                publishedfileid = (ulong)workshopId,
                                appid = APPID,
                                filename = "gmpublish.gma",
                                title = addonInfo.title,
                                file_description = addonInfo.description.Description,
                                visibility = (uint)EPublishedFileVisibility.Public,
                            };
                            foreach (var tag in addonInfo.description.Tags) { request.tags.Add(tag); }

                            // only update the icon if one was supplied
                            if(iconFile != null && iconFile.Exists){
                                request.image_height = 512;
                                request.image_width = 512;
                                request.preview_filename = "gmpublish_icon.jpg";
                            }

                            var updateCallback = await publishService.SendMessage(publish => publish.Update(request));
                            var updateResponse = updateCallback.GetDeserializedResponse<CPublishedFile_Update_Response>();

                            
                            Console.WriteLine("Success! Addon " + workshopId + " has been updated");
                            Exit(1); // 1 signals success in the original gmpublish
                        }

                    } else if(currentAction == Action.LIST) {
                        var publishService = steamUnifiedMessages.CreateService<IPublishedFile>();

                        var request = new CPublishedFile_GetUserFiles_Request
                        {
                            appid = APPID,
                            ids_only = false,
                            steamid = steamClient.SteamID.ConvertToUInt64()
                            
                        };

                        Console.WriteLine("Getting published files...\n");

                        var listCallback = await publishService.SendMessage(publish => publish.GetUserFiles(request));
                        var listResponse = listCallback.GetDeserializedResponse<CPublishedFile_GetUserFiles_Response>();
                        Console.WriteLine("Found " + listResponse.total.ToString() + " results");
                        listResponse.publishedfiledetails.ForEach( publishedFile =>
                            Console.WriteLine(String.Format("\t{0}\t{1,-5:F1} MB \"{2}\" ", publishedFile.publishedfileid, publishedFile.file_size / 1000000f, publishedFile.title))
                        );
                        Exit(1); // 1 signals success in the original gmpublish
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Exit(2);
                }
            });
            task.Wait();
            steamUser.LogOff();
            isRunning = false;
        }

        #region SteamLogin
        static void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Unable to connect to Steam: {0}", callback.Result);

                isRunning = false;
                return;
            }

            Console.WriteLine("Connected to Steam! Logging in '{0}'...", user);

            byte[] sentryHash = null;
            if (File.Exists("sentry.bin"))
            {
                // if we have a saved sentry file, read and sha-1 hash it
                byte[] sentryFile = File.ReadAllBytes("sentry.bin");
                sentryHash = CryptoHelper.SHAHash(sentryFile);
            }

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = user,
                Password = pass,

                // our subsequent logons use the hash of the sentry file as proof of ownership of the file
                // this will also be null for our first (no authcode) and second (authcode only) logon attempts
                SentryFileHash = sentryHash,
            });
        }
        static void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Console.WriteLine("Disconnected from Steam. Unable to continue");
        }

        static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            bool isSteamGuard = callback.Result == EResult.AccountLogonDenied;
            bool is2FA = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

            if (isSteamGuard || is2FA)
            {
                Console.WriteLine("This account is SteamGuard protected! This tool is intended to be used for automating workshop uploads and therefore requires an account with steamguard disabled.");
                Exit(63);
                return;
            }

            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;
                return;
            }

            Console.WriteLine("Successfully logged on!");
            FullyLoggedIn(callback);
        }

        static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        static void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            Console.WriteLine("Updating sentryfile...");

            // write out our sentry file
            // ideally we'd want to write to the filename specified in the callback
            // but then this sample would require more code to find the correct sentry file to read during logon
            // for the sake of simplicity, we'll just use "sentry.bin"

            int fileSize;
            byte[] sentryHash;
            using (var fs = File.Open("sentry.bin", FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                fs.Seek(callback.Offset, SeekOrigin.Begin);
                fs.Write(callback.Data, 0, callback.BytesToWrite);
                fileSize = (int)fs.Length;

                fs.Seek(0, SeekOrigin.Begin);
                using (var sha = new SHA1CryptoServiceProvider())
                {
                    sentryHash = sha.ComputeHash(fs);
                }
            }

            // inform the steam servers that we're accepting this sentry file
            steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = fileSize,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sentryHash,
            });

            Console.WriteLine("Done!");
        }
        #endregion
    }
}
