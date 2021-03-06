﻿using MusicBeePlugin.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml;
using WebServer;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
	class GenerateResponse
	{
		public static MusicBeeApiInterface MbApi { get; set; }

		private const string LASTFMAPIKEY = "a7fbad25e1b14c126988a52cca42b4c1";

		static NowPlayingData data = null;
		private static WSServer _wsref;

		public static WSServer WsRef
		{
			get { return _wsref; }
			set { _wsref = value; }
		}


		public static void Init()
		{

		}

		/// <summary>
		/// Calls MusicBee api function depending on the response
		/// </summary>
		/// <param name="response"></param>
		public static void HandleResponse(string response)
		{
			if (response is null)
			{
				return;
			}

			switch (response)
			{
				case "toggleplay":
					MbApi.Player_PlayPause();
					break;

				case "playprev":
					MbApi.Player_PlayPreviousTrack();
					break;

				case "playnext":
					MbApi.Player_PlayNextTrack();
					break;

				case "queryFiles":
					QueryFiles();
					break;

				default:
					break;
			}

			//if the response contains "/" backslash then it contain multiple values
			if (response.Contains("/"))
			{
				//Create an array of responses
				string[] responseArray = response.Split('/');
				if (responseArray.Length == 2)
				{
					//Array of values that are passed with the request
					var value = responseArray[1];
					switch (responseArray[0])
					{
						case "settrackpos":
							SetPlayerTrackPos(float.Parse(value, CultureInfo.InvariantCulture.NumberFormat));
							break;

						case "setvol":
							SetVolume(float.Parse(value, CultureInfo.InvariantCulture.NumberFormat));
							break;

						case "getSimilarArtist":
							GetSimilarArtists(value);
							break;

						case "queryFiles":
							QueryFiles(value);
							break;

						default:
							break;
					}
				}
			}
		}

		public static Stream GetCurrentArtworkBlur()
		{
			using (Image img = Image.FromStream(GetCurrentArtwork()))
			{
				using (Bitmap b = new Bitmap(img, new Size(100, 100)))
				{
					var blur = new SuperfastBlur.GaussianBlur(b);
					var blurredImage = blur.Process(10);
					byte[] bin;
					using (MemoryStream ms2 = new MemoryStream())
					{
						blurredImage.Save(ms2, System.Drawing.Imaging.ImageFormat.Jpeg);
						bin = ms2.ToArray();
					}
					return new MemoryStream(bin);
				}
			}

		}

		public static Stream GetCurrentArtwork()
		{
			return GetArtwork(MbApi.NowPlaying_GetFileUrl());
		}

		private static void GetSimilarArtists(string value)
		{
			try
			{
				SimilarArtists similarArtistList = new SimilarArtists();
				similarArtistList.Data = MbApi.Library_QuerySimilarArtists(value, 0.2).Split('\u0000');
				similarArtistList.callback_function = "updateSimilarArtistList";

				WsRef.SendMessage(Util.Serialize(similarArtistList));
			}
			catch (Exception){ }
		}

		public static AlbumList QueryFiles(string query = "")
		{
			try
			{
				string[] allFiles = { };
				MbApi.Library_QueryFilesEx(query, ref allFiles);
				MetaDataType[] fields = {
					MetaDataType.Album,
					MetaDataType.AlbumArtist,
					MetaDataType.Artist,
					MetaDataType.TrackNo,
					MetaDataType.Rating,
					MetaDataType.TrackTitle,
					MetaDataType.RatingLove,
				};

				List<TrackData> allTrackData= new List<TrackData>();

				for (int i = 0; i < allFiles.Length; i++)
				{
					string[] fileTags = { };
					MbApi.Library_GetFileTags(allFiles[i], fields, ref fileTags);

					allTrackData.Add(new TrackData()
					{
						FilePath = allFiles[i],
						Album = fileTags[0],
						AlbumArtist = fileTags[1],
						Artist = fileTags[2],
						TrackNo = fileTags[3],
						Rating = fileTags[4],
						TrackTitle = fileTags[5],
						Loved = fileTags[6],
					});
				}

				var GroupedTrackList = allTrackData.GroupBy(t => new { t.Album, t.AlbumArtist }).Select(
					albm => new AlbumData() {
						Album = albm.Key.Album,
						AlbumArtist = albm.Key.AlbumArtist,
						TrackList = albm.ToList(),
					}).OrderBy(x => x.Album).ToList();

				AlbumList trackLists = new AlbumList() { callback_function = "fileQueryComplete", AlbumLists = GroupedTrackList };


				return trackLists;
			}
			catch (Exception e)
			{
				throw e;
			}
		}



		/// <summary>
		/// Generates serialized JSON object with Now playing data to send to the browser
		/// </summary>
		/// <param name="isForcedOnce">Should we send the artwork data</param>
		/// <returns>Serialized json data</returns>
		public static string GetNowPlayingData(bool isForcedOnce)
		{
			try
			{
				float currentPos = MbApi.Player_GetPosition();
				float totalTime = MbApi.NowPlaying_GetDuration();
				float completed = currentPos / totalTime * 100;

				if (data == null)
					data = new NowPlayingData();

				string oldHash = Util.CreateMD5(data.CurrentTrackTitle + data.CurrentTrackArtist).ToLower();
				string curHash = Util.CreateMD5(MbApi.NowPlaying_GetFileTag(MetaDataType.TrackTitle) +
						MbApi.NowPlaying_GetFileTag(MetaDataType.Artist)).ToLower();
				bool TrackChanged = (curHash != oldHash);

				data.HasTrackChanged = (TrackChanged || isForcedOnce);
				data.CurrentTrackTitle = MbApi.NowPlaying_GetFileTag(MetaDataType.TrackTitle);
				data.CurrentTrackArtist = MbApi.NowPlaying_GetFileTag(MetaDataType.Artist);
				data.CurrentPlayerState = MbApi.Player_GetPlayState().ToString();
				data.CurrentTrackSize = totalTime;
				data.CurrentTrackPosition = currentPos;
				data.CurrentTrackCompleted = completed;
				data.CurrentTrackSizeReadable = Util.FormattedMills(totalTime);
				data.CurrentTrackPositionReadable = Util.FormattedMills(currentPos);
				data.CurrentTrackAlbum = MbApi.NowPlaying_GetFileTag(MetaDataType.Album);
				data.CurrentTrackGenre = MbApi.NowPlaying_GetFileTag(MetaDataType.Genres);
				data.ArtworkPath = (TrackChanged || isForcedOnce) ? MbApi.NowPlaying_GetFileUrl() : string.Empty;
				data.CurrentVolume = MbApi.Player_GetVolume();

				var allfile = MbApi.NowPlayingList_QueryGetNextFile();
				data.NextQueueTrack = allfile;

				data.ArtistDataset = (TrackChanged || isForcedOnce) ? GetArtistData(MbApi.NowPlaying_GetFileTag(MetaDataType.Artist)) : new ArtistData();

				data.callback_function = "refreshPlayerControl";

				return Util.Serialize(data);
			}
			catch (Exception) { }
			return string.Empty;
		}

		private static void SetPlayerTrackPos(float pos)
		{
			int position = Convert.ToInt32(pos / 100 * MbApi.NowPlaying_GetDuration());
			MbApi.Player_SetPosition(position);
		}


		/// <summary>
		/// Get the resized album artwork and return it as stream
		/// </summary>
		/// <param name="filePath"></param>
		/// <returns>Stream</returns>
		public static Stream GetArtwork(string filePath, int width = 300, int height = 300)
		{
			TagLib.File file;
			try
			{
				file = TagLib.File.Create(filePath);
			}
			catch (Exception)
			{
				file = TagLib.File.Create(Path.Combine(
					Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
					"web\\img\\artwork-default.png"));
			}

			if (file.Tag.Pictures.Length >= 1)
				{
					var bin = file.Tag.Pictures[0].Data.Data;
					using (MemoryStream ms = new MemoryStream(bin, 0, bin.Length))
					{
						using (Image img = Image.FromStream(ms))
						{
							using (Bitmap b = new Bitmap(img, new Size(width, height)))
							{
								using (MemoryStream ms2 = new MemoryStream())
								{
									b.Save(ms2, System.Drawing.Imaging.ImageFormat.Jpeg);
									bin = ms2.ToArray();
								}
							}
						}
					}
					return new MemoryStream(bin);
				}
			return null;
		}

		public static AlbumList GetNowPlaylist()
		{
			string[] allFiles = { };
			MbApi.NowPlayingList_QueryFilesEx("", ref allFiles);
			MetaDataType[] fields = {
					MetaDataType.Album,
					MetaDataType.AlbumArtist,
					MetaDataType.Artist,
					MetaDataType.TrackNo,
					MetaDataType.Rating,
					MetaDataType.TrackTitle,
					MetaDataType.RatingLove,
				};


			List<TrackData> allTrackData = new List<TrackData>();

			for (int i = 0; i < allFiles.Length; i++)
			{
				string[] fileTags = { };
				MbApi.Library_GetFileTags(allFiles[i], fields, ref fileTags);

				allTrackData.Add(new TrackData()
				{
					FilePath = allFiles[i],
					Album = fileTags[0],
					AlbumArtist = fileTags[1],
					Artist = fileTags[2],
					TrackNo = fileTags[3],
					Rating = fileTags[4],
					TrackTitle = fileTags[5],
					Loved = fileTags[6],
				});
			}

			var GroupedTrackList = allTrackData.GroupBy(t => new { t.Album, t.AlbumArtist }).Select(
				albm => new AlbumData()
				{
					Album = albm.Key.Album,
					AlbumArtist = albm.Key.AlbumArtist,
					TrackList = albm.ToList(),
				}).ToList();

			return new AlbumList() {
				callback_function = "updatePlaylistData", AlbumLists = GroupedTrackList };
		}


		public static void PlayTrack(string url)
		{
			MbApi.NowPlayingList_PlayNow(url);
		}

		/// <summary>
		/// Sets the MusicBee's volume with the value
		/// </summary>
		/// <param name="value"></param>
		private static void SetVolume(float value)
		{
			//MusicBee uses 0 to 1 volume range so clamp the values
			value = Mathf.Range(value, 0, 1);
			MbApi.Player_SetVolume(value);
		}


		/// <summary>
		/// Gets artist data such as biodata summery
		/// </summary>
		/// <param name="artistName"></param>
		/// <returns></returns>
		private static ArtistData GetArtistData(string artistName)
		{
			using (WebClient client = new WebClient())
			{
				try
				{
					string url = string.Format("http://ws.audioscrobbler.com/2.0/?method=artist.getinfo&artist={0}&api_key={1}&format=xml",
										Uri.EscapeUriString(artistName),
										LASTFMAPIKEY);
					client.Encoding = System.Text.Encoding.UTF8;
					string xmlString = client.DownloadString(url);
					if (!string.IsNullOrEmpty(xmlString))
					{

						XmlDocument doc = new XmlDocument();
						doc.LoadXml(xmlString);

						return new ArtistData()
						{
							ArtistInfo = doc.SelectSingleNode("lfm/artist/bio/summary").InnerText,
						};
					}
				}
				catch (Exception)
				{
					return new ArtistData() { ArtistInfo = "No artist info found" };
				}
			}
			return null;
		}
	}
}
