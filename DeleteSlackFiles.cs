using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace DeleteSlackFiles.Function
{
    public static class DeleteSlackFiles
    {
        private static readonly HttpClient client = new HttpClient();
        private static readonly string token = System.Environment.GetEnvironmentVariable("Token", EnvironmentVariableTarget.Process);
        private static ILogger logger;

        [FunctionName("DeleteSlackFiles")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            logger = log;
            var slackResponse = new SlackResponse
            {
                text = $"",
                icon_emoji = ":yami:",
                username = "ファイル削除bot",
                link_names = "1"
            };

            string bodyContent = await new StreamReader(req.Body).ReadToEndAsync();
            var values = System.Web.HttpUtility.ParseQueryString(bodyContent);
            var jsonContent = JsonConvert.SerializeObject(values.AllKeys.ToDictionary(k => k, k => values[k]));

            var slackRequest =  JsonConvert.DeserializeObject<SlackRequest>(jsonContent);

            if (slackRequest.text.Contains("--help") || slackRequest.text.Contains("-h")) return HelpActionResult();

            // list request
            var fileListRequest = ToFileListRequst(slackRequest);
            if (ValidateFileListRequest(fileListRequest) != null)
            {
                slackResponse.text = ValidateFileListRequest(fileListRequest);
                return (ActionResult)new OkObjectResult(JsonConvert.SerializeObject(slackResponse));
            }

            var listResponse = await ExecuteGetFileListRequestAsync(fileListRequest);
            if (listResponse.files.Count == 0)
            {
                slackResponse.text = $"削除対象のファイルが0件です";
                return (ActionResult)new OkObjectResult(JsonConvert.SerializeObject(slackResponse));
            }


            // delete request
            var files = listResponse.files;
            var deleteMessage = await ExecuteDeleteFileListRequestAsync(files);
            slackResponse.text = deleteMessage;

            return (ActionResult)new OkObjectResult(JsonConvert.SerializeObject(slackResponse));
        }

        private static IActionResult HelpActionResult()
        {
            var text = $@"
```
ファイル一括削除
delete-files [START_DATE] [END_DATE] [--all-channels | -ac] [--all-users | -au]
-ac, --all-channels 全publicチャンネルを削除対象とする
-au, --all-user 全ユーザーのファイルを削除対象とする
-h, --help ヘルプ
```";

            var slackResponse = new SlackResponse
            {
                text = text,
                icon_emoji = ":yami:",
                username = "ファイル削除bot",
                link_names = "1"
            };

            return (ActionResult) new OkObjectResult(JsonConvert.SerializeObject(slackResponse));
        }

        private static List<string> ToSlackRequestTextList(String text)
        {
            var pattern = @"( |　)+";
            var replaced = new Regex(pattern).Replace(text, " ");
            return replaced.Split(' ').ToList();
        }
        
        private static TimeSpan? ToTimeSpan(string dateStr) {
            logger.LogInformation(dateStr);
            DateTime dateTime;
            if (DateTime.TryParse(dateStr, out dateTime)) {
                return dateTime.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }
            return null;
        }

         private static TimeSpan? GetTimeStampFrom(List<string> textList)
        {
            var fromIndex = 1;
            if (fromIndex >= textList.Count) {
                return null;
            }

            return ToTimeSpan(textList[fromIndex]);
        }

        private static TimeSpan? GetTimeStampTo(List<string> textList)
        {
            var toIndex = 2;
            if (toIndex >= textList.Count) {
                return null;
            }

            return ToTimeSpan(textList[toIndex]);
        }

        // 指定なしなら投稿されたチャンネルのみを削除
        private static string GetTargetChannel(List<string> textList, String channelId)
        {
            return textList.Contains("--all-channels") || textList.Contains("-ac")
                ? null
                : channelId;
        }

        // 指定なしなら投稿ユーザー=自身のファイルのみを削除
        private static string GetTargetUser(List<string> textList, String userId)
        {
            return textList.Contains("--all-users") || textList.Contains("-au")
                ? null
                : userId;
        }

        private static FileListRequest ToFileListRequst(SlackRequest slackRequest)
        {
            var slackRequestTextList = ToSlackRequestTextList(slackRequest.text);

            var timeStampFrom = GetTimeStampFrom(slackRequestTextList);
            var timeStampTo = GetTimeStampTo(slackRequestTextList);
            var channel = GetTargetChannel(slackRequestTextList, slackRequest.channel_id);
            var user = GetTargetUser(slackRequestTextList, slackRequest.user_id);

            var request = new FileListRequest
            {
                Token = token,
                From = timeStampFrom,
                To = timeStampTo,
                Channel = channel,
                User = user
            };

            return request;
        }

        // TODO: エラーthrowして返す
        #nullable enable
        private static string? ValidateFileListRequest(FileListRequest fileListRequest)
        {
            if (fileListRequest.From == null)
            {
                return "削除範囲開始日時が指定されていないか、不正です";
            }

            if (fileListRequest.To == null)
            {
                return "削除範囲終了日時が指定されていないか、不正です";
            }

            if (fileListRequest.From > fileListRequest.To)
            {
                return "削除範囲開始日時が終了日時より後になっています。";
            }

            return null;
        }
        #nullable disable

        private static async Task<FileListResponse> ExecuteGetFileListRequestAsync(FileListRequest fileListRequest)
        {
            var uri = $"https://slack.com/api/files.list?token={token}&ts_from={fileListRequest.From?.TotalSeconds.ToString()}&ts_to={fileListRequest.To?.TotalSeconds.ToString()}";

            if (!string.IsNullOrEmpty(fileListRequest.Channel)) {
                logger.LogInformation(fileListRequest.Channel);
                uri += $"&channel={fileListRequest.Channel}";
            }
            if (!string.IsNullOrEmpty(fileListRequest.User)) {
                logger.LogInformation(fileListRequest.User);
                uri += $"&user={fileListRequest.User}";
            }
            logger.LogInformation(uri);
            var listHttpResponse = await client.GetAsync(uri);
            logger.LogInformation(await listHttpResponse.Content.ReadAsStringAsync());
            return await listHttpResponse.Content.ReadAsAsync<FileListResponse>();
        }

        private static async Task<String> ExecuteDeleteFileListRequestAsync(List<File> files) {
            const string deleteRequestURI = "https://slack.com/api/files.delete";
            logger.LogInformation(deleteRequestURI);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            int deletedCount = 0;
            foreach (var file in files)
            {
                var deleteRequest = new FileDeleteRequest
                {
                    file = file.id
                };
                var deleteRequestJson = JsonConvert.SerializeObject(
                    new FileDeleteRequest
                    {
                        file = file.id
                    });
                var content = new StringContent(deleteRequestJson, Encoding.UTF8, "application/json");

                var deleteHttpResponse = await client.PostAsync(deleteRequestURI, content);
                logger.LogInformation(await deleteHttpResponse.Content.ReadAsStringAsync());
                var deleteResponse = await deleteHttpResponse.Content.ReadAsAsync<FileDeleteResponse>();

                if (!deleteResponse.ok)
                {
                    return  $"{deletedCount.ToString()}ファイルを削除しました。Error: Deleting {file.name} is failed";
                }
                 deletedCount++;
            }

            return  $"{deletedCount.ToString()}ファイルを削除しました。";
        }

        // Entites
        public class SlackResponse
        {
            public string text { get; set; }
            public string icon_emoji { get; set; }
            public string username { get; set; }
            public string link_names { get; set; }
        }

        public class SlackRequest
        {
            public string token { get; set; }
            public string team_id { get; set; }
            public string team_domain { get; set; }
            public string channel_id { get; set; }
            public string channel_name { get; set; }
            public string TimeSpan { get; set; }
            public string user_id { get; set; }
            public string user_name { get; set; }
            public string text { get; set; }
            public string trigger_word { get; set; }
        }

        public class FileDeleteRequest
        {
            public string file { get; set; }
        }

        public class FileDeleteResponse
        {
            public bool ok { get; set; }
        }

        public class FileListResponse
        {
            public bool ok { get; set; }
            public List<File> files { get; set; }
            public Paging paging { get; set; }
        }

        #nullable enable
         public class FileListRequest
        {
            public string? User { get; set; }
            public string Token { get; set; } = "";
            public string? Channel { get; set; }
            public TimeSpan? From { get; set; }
            public TimeSpan? To { get; set; }
        }
        #nullable disable

        public class File
        {
            public string id { get; set; }
            public int created { get; set; }
            public int TimeSpan { get; set; }
            public string name { get; set; }
            public string title { get; set; }
            public string mimetype { get; set; }
            public string filetype { get; set; }
            public string pretty_type { get; set; }
            public string user { get; set; }
            public bool editable { get; set; }
            public int size { get; set; }
            public string mode { get; set; }
            public bool is_external { get; set; }
            public string external_type { get; set; }
            public bool is_public { get; set; }
            public bool public_url_shared { get; set; }
            public bool display_as_bot { get; set; }
            public string username { get; set; }
            public string url_private { get; set; }
            public string url_private_download { get; set; }
            public string thumb_64 { get; set; }
            public string thumb_80 { get; set; }
            public string thumb_360 { get; set; }
            public int thumb_360_w { get; set; }
            public int thumb_360_h { get; set; }
            public string thumb_480 { get; set; }
            public int thumb_480_w { get; set; }
            public int thumb_480_h { get; set; }
            public string thumb_160 { get; set; }
            public string thumb_720 { get; set; }
            public int thumb_720_w { get; set; }
            public int thumb_720_h { get; set; }
            public string thumb_800 { get; set; }
            public int thumb_800_w { get; set; }
            public int thumb_800_h { get; set; }
            public string thumb_960 { get; set; }
            public int thumb_960_w { get; set; }
            public int thumb_960_h { get; set; }
            public string thumb_1024 { get; set; }
            public int thumb_1024_w { get; set; }
            public int thumb_1024_h { get; set; }
            public int image_exif_rotation { get; set; }
            public int original_w { get; set; }
            public int original_h { get; set; }
            public string permalink { get; set; }
            public string permalink_public { get; set; }
            public List<String> channels { get; set; }
            public List<String> groups { get; set; }
            public List<String> ims { get; set; }
            public int comments_count { get; set; }
        }

        public class Paging
        {
            public int count { get; set; }
            public int total { get; set; }
            public int page { get; set; }
            public int pages { get; set; }
        }
    }
}