﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json;
using Microsoft.ContentModerator.BusinessEntities;
using System.Threading.Tasks;
using Microsoft.ContentModerator.BusinessEntities.Entities;
using Microsoft.ContentModerator.BusinessEntities.CustomExceptions;
using Microsoft.ContentModerator.RESTUtilityServices;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Microsoft.ContentModerator.FFMPEG
{

	/// <summary>
	/// Represents a FrameGeneratorService.
	/// </summary>
	public class FrameGenerator
	{
		private AmsConfigurations _amsConfig;
		private string _videoPublishUri = string.Empty;
		private string _videoName = string.Empty;
		private string _reviewId = string.Empty;
		private string _videoContainerName = string.Empty;
		private double _confidence;
		CloudBlobClient _blobClient = null;
		string _blobContainerName = string.Empty;
		CloudBlobContainer _container = null;

		List<FrameEventDetails> _frameEventsSource = null;
		private VideoReviewApi _reviewApIobj = null;

		public CloudStorageAccount StorageAccount { get; set; } = null;

		/// <summary>
		/// Instaiates an instance of Frame generator.
		/// </summary>
		/// <param name="config"></param>
		/// <param name="confidenceVal"></param>
		public FrameGenerator(AmsConfigurations config, string confidenceVal)
		{
			this._amsConfig = config;
			_reviewApIobj = new VideoReviewApi(config);
			_frameEventsSource = new List<FrameEventDetails>();
			StorageAccount = CloudStorageAccount.Parse(this._amsConfig.BlobConnectionString);
			_blobClient = StorageAccount.CreateCloudBlobClient();
			_confidence = Convert.ToDouble(confidenceVal);
		}

		#region Generate Frames

		/// <summary>
		/// Generates And Submit Frames
		/// </summary>
		/// <param name="assetInfo">assetInfo</param>
		/// <returns>Retruns Review Id</returns>
		public List<FrameEventDetails> GenerateAndSubmitFrames(UploadAssetResult assetInfo, ref string reviewId)
		{
			List<FrameEventDetails> frameEventsList = new List<FrameEventDetails>();
			string reviewVideoRequestJson = string.Empty;
			try
			{

				PopulateFrameEvents(assetInfo.ModeratedJson, frameEventsList);
				reviewVideoRequestJson = _reviewApIobj.CreateReviewRequestObject(assetInfo, frameEventsList);

				_reviewId =
					JsonConvert.DeserializeObject<List<string>>(_reviewApIobj.ExecuteCreateReviewApi(reviewVideoRequestJson).Result)
						.FirstOrDefault();
				reviewId = _reviewId;
				_blobContainerName = _reviewId;

				_container = _blobClient.GetContainerReference(_blobContainerName);
				_container.CreateIfNotExists();
				_container.SetPermissions(new BlobContainerPermissions {PublicAccess = BlobContainerPublicAccessType.Blob});

				foreach (var item in frameEventsList)
				{
					item.FrameName = reviewId + item.FrameName;
				}
                
				return GenerateAndUploadFrameImages(frameEventsList,assetInfo);
			}
			catch (Exception ex)
			{
				Console.WriteLine("EXCEPTION HAPPENED AT METHOD : {0} , for Review Id : {1} ", MethodBase.GetCurrentMethod().Name,
					reviewId);
				Console.WriteLine("EXCEPTION DETAILS : {0}", ex);
				Console.WriteLine(ex);
				throw new FrameGenerationException()
				{
					ReviewId = string.Empty,
					VideoName = assetInfo.VideoName,
					AssetId = assetInfo.AssetId,
					ErrorTitle = Constants.ErrorTitle,
					ErrorReason = ex.Message
				};
			}
			

		}

        #endregion

        #region FrameImage Generation

        /// <summary>
        /// Generates frames based on moderated json source.
        /// </summary>
        /// <param name="moderatedJsonstring">moderatedJsonstring</param>
        /// <param name="resultEventDetailsList">resultEventDetailsList</param>

        private void PopulateFrameEvents(string moderatedJsonstring, List<FrameEventDetails> resultEventDetailsList)
        {
            if (UploadAssetResult.V2JSONPath != null)
            {
                try
                {
                    using (var streamReader = new StreamReader(UploadAssetResult.V2JSONPath))
                    {
                        string jsonv2 = streamReader.ReadToEnd();
                        moderatedJsonstring = jsonv2;
                    }

                }
                catch
                {
                    Console.WriteLine("Json file associated with video is not present. V2 Json needs to be in the same folder as video with same name with .json extension.");
                }
                var moderatedJsonV2 = JsonConvert.DeserializeObject<VideoModerationResult>(moderatedJsonstring);

                if (moderatedJsonV2.Shots != null)
                {
                    double ticks = Convert.ToDouble(moderatedJsonV2.TimeScale);
                    int timescale = Convert.ToInt32(moderatedJsonV2.TimeScale);
                    int frameCount = 0;
                    foreach (var shot in moderatedJsonV2.Shots)
                    {
                        if (shot.Clips != null)
                        {
                            foreach (var clip in shot.Clips)
                            {
                                if (clip.Frames != null)
                                {

                                    foreach (var frameObj in clip.Frames)
                                    {
                                        if (Convert.ToDouble(frameObj.AdultConfidence) > _confidence)
                                        {
                                            var eventDetailsObj = new FrameEventDetails
                                            {
                                                TimeStamp = frameObj.TimeStamp,
                                                IsAdultContent = frameObj.IsAdultContent,
                                                AdultConfidence = frameObj.AdultConfidence,
                                                Index = frameObj.Index,
                                                TimeScale = timescale,
                                                IsRacyContent = frameObj.IsRacyContent,
                                                RacyConfidence = frameObj.RacyConfidence,

                                            };
                                            frameCount++;
                                            eventDetailsObj.FrameName = "_" + frameCount + ".png";
                                            resultEventDetailsList.Add(eventDetailsObj);

                                        }
                                    }
                                }
                            }
                        }
                    }


                }
            }
            else
            {
                var jsonModerateObject = JsonConvert.DeserializeObject<VideoModerationResult>(moderatedJsonstring);

                if (jsonModerateObject != null)
                {
                    var timeScale = Convert.ToString(jsonModerateObject.TimeScale);
                    var timescale = Convert.ToInt32(timeScale);

                    int frameCount = 0;
                    foreach (var item in jsonModerateObject.Fragments)
                    {
                        if (item.Events != null)
                        {
                            foreach (var events in item.Events)
                            {
                                foreach (FrameEventDetails eventObj in events)
                                {
                                    if (Convert.ToDouble(eventObj.AdultConfidence) > _confidence)
                                    {
                                        var eventDetailsObj = new FrameEventDetails
                                        {
                                            TimeStamp = eventObj.TimeStamp,
                                            IsAdultContent = eventObj.IsAdultContent,
                                            AdultConfidence = eventObj.AdultConfidence,
                                            Index = eventObj.Index,
                                            TimeScale = timescale
                                        };
                                        frameCount++;
                                        eventDetailsObj.FrameName = "_" + frameCount + ".png";
                                        resultEventDetailsList.Add(eventDetailsObj);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }


	    /// <summary>
	    ///  GetGeneratedFrameList method used for Generating Frames using Moderated Json 
	    /// </summary>
	    /// <param name="eventsList">resultDownloaddetailsList</param>
	    /// <param name="assetInfo"></param>
	    private List<FrameEventDetails> GenerateAndUploadFrameImages(List<FrameEventDetails> eventsList,UploadAssetResult assetInfo)
		{
			#region frameCreation

			string frameStorageLocalPath = this._amsConfig.FfmpegFramesOutputPath + _reviewId;
			Directory.CreateDirectory(frameStorageLocalPath);

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine("\n Video Frames Creation inprogress...");
            Stopwatch sw = new Stopwatch();
            sw.Start();
			#region Check FFMPEG.Exe

			string ffmpegBlobUrl=string.Empty;

            if (File.Exists(this._amsConfig.FfmpegExecutablePath))
            {
                ffmpegBlobUrl = this._amsConfig.FfmpegExecutablePath;
            }           

		    #endregion
            

            List<string> args = new List<string>();
            StringBuilder sb = new StringBuilder();
            int frameCounter = 0;
            foreach (var frame in eventsList)
            {
                TimeSpan ts = TimeSpan.FromSeconds(Convert.ToDouble(frame.TimeStamp / frame.TimeScale));
                var line = "-ss " + ts + " -i " + assetInfo.VideoFilePath + " -map " + frameCounter + ":v -frames:v 1 "  + frameStorageLocalPath + "\\" + frame.FrameName + " ";
                frameCounter++;
                sb.Append(line);
                if(sb.Length > 30000)
                {
                    args.Add(sb.ToString());
                    sb.Clear();
                    frameCounter = 0;
                }
            }
            if(sb.Length != 0)
            {
                args.Add(sb.ToString());
            }

            Parallel.ForEach(args, new ParallelOptions { MaxDegreeOfParallelism = 4 }, 
                arg => CreateTaskProcess(arg, ffmpegBlobUrl));			
			
            sw.Stop();
            using (var stw = new StreamWriter("AmsPerf.txt", true))
            {
                stw.WriteLine("Frame Creation Elapsed time: {0}", sw.Elapsed);
            }
            Console.ForegroundColor = ConsoleColor.DarkGreen;
			Console.WriteLine(" Frames(" + eventsList.Count() + ") created successfully.");
	

            Parallel.ForEach(eventsList, 
                new ParallelOptions { MaxDegreeOfParallelism = 4 }, 
                evnt => AddFrameToBlobGenerationProcess(evnt, frameStorageLocalPath + "\\" + evnt.FrameName));

            Console.ForegroundColor = ConsoleColor.DarkGreen;
			Console.WriteLine(" Frames(" + eventsList.Count() + ") uploaded successfully ");

			if (Directory.Exists(frameStorageLocalPath))
			{
				Directory.Delete(frameStorageLocalPath, true);
			}

			#endregion

			return eventsList;
		}

        /// <summary>
        /// Upload frames to blob
        /// </summary>
        /// <param name="frame"></param>
        /// <param name="imagePath"></param>
        /// <returns></returns>
        private string AddFrameToBlobGenerationProcess(FrameEventDetails frame, string imagePath)
        {
            try
            {
                FileInfo fileInfo = new FileInfo(imagePath);


                if (fileInfo != null && fileInfo.Length > 0)
                {
                    byte[] imageData = null;
                    long imageFileLength = fileInfo.Length;
                    using (FileStream fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                    {
                        BinaryReader br = new BinaryReader(fs);
                        imageData = br.ReadBytes((int)imageFileLength);
                    }

                    using (Stream stream = new MemoryStream(imageData))
                    {
                        CloudBlockBlob blockBlob = _container.GetBlockBlobReference(Path.GetFileName(imagePath));
                        blockBlob.UploadFromStream(stream);
                        frame.PrimaryUri = blockBlob.StorageUri.PrimaryUri.ToString();
                    }
                }

                return frame.PrimaryUri;

            }
            catch (Exception e)
            {
                Console.WriteLine("{0}-{1}", "Failed to Upload Frames to Blob Storage", e.Message);
                throw;

            }

        }
		

		/// <summary>
		/// Frame generation using ffmpeg
		/// </summary>
		/// <param name="eventTimeStamp"></param>
		/// <param name="keyframefolderpath"></param>
		/// <param name="timescale"></param>
		/// <param name="framename"></param>
		/// <param name="ffmpegBlobUrl"></param>
		private void CreateTaskProcess(string arg, string ffmpegBlobUrl)
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo();
			processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
			processStartInfo.FileName = ffmpegBlobUrl;
            processStartInfo.Arguments = arg;
			var process = Process.Start(processStartInfo);
			process.WaitForExit();
		}

		/// <summary>
		/// Download ffmpeg exe to local
		/// </summary>
		/// <param name="fileName"></param>
		/// <param name="path"></param>
		private void DownloadFileFromBlob(string fileName, string path)
		{
			CloudStorageAccount storageAccount = CloudStorageAccount.Parse(this._amsConfig.BlobConnectionString);
			CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
			CloudBlobContainer container = blobClient.GetContainerReference(this._amsConfig.BlobContainerForFfmpeg);
			container.CreateIfNotExists();
			container.SetPermissions(new BlobContainerPermissions { PublicAccess = BlobContainerPublicAccessType.Blob });
			CloudBlockBlob blockBlob = container.GetBlockBlobReference(fileName);
			Stream stream = new MemoryStream();
			blockBlob.DownloadToStream(stream);
			stream.Position = 0;
			if (stream != null)
			{
				FileStream fileStream = File.Create(path);
				stream.Position = 0;
				stream.CopyTo(fileStream);
				fileStream.Close();
			}
		}

		private string AppendTimeStamp(string fileName)
		{
			return string.Concat(
				Path.GetFileNameWithoutExtension(fileName),
				DateTime.Now.ToString("yyMMddHHMMssfff"),
				Path.GetExtension(fileName)
				);
		}

		#endregion


	}
}
