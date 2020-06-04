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

            string bodyContent = await new StreamReader(req.Body).ReadToEndAsync();
            var values = System.Web.HttpUtility.ParseQueryString(bodyContent);
            var jsonContent = JsonConvert.SerializeObject(values.AllKeys.ToDictionary(k => k, k => values[k]));

            var slackRequest =  JsonConvert.DeserializeObject<SlackRequest>(jsonContent);

            if (slackRequest.text.Contains("--help") || slackRequest.text.Contains("-h")) return HelpActionResult();

            var listResponse = await ExecuteGetFileListRequestAsync(slackRequest);
            if (listResponse.files.Count == 0) {
                var slackResponse = new SlackResponse
                {
                    text = $"削除対象のファイルが0件です",
                    icon_emoji = ":yami:",
                    username = "ファイル削除bot",
                    link_names = "1"
                };
                
                return (ActionResult)new OkObjectResult(JsonConvert.SerializeObject(slackResponse));
            }

            var totalCount = listResponse.paging.total;
            var files = listResponse.files;
            var deleteMessage = await ExecuteDeleteFileListRequestAsync(files);

            return (ActionResult)new OkObjectResult(JsonConvert.SerializeObject(new SlackResponse
            {
                text = deleteMessage,
                icon_emoji = ":yami:",
                username = "ファイル削除bot",
                link_names = "1"
            }));
        }

        private static IActionResult HelpActionResult()
        {
            var text = $@"
```
ファイル一括削除
ex. delete-file -t 2020/01/01 -ac
-t, --to 削除対象ファイルのアップロード日時範囲指定 範囲終了日時  (指定しない場合: 実行日までの全ファイルが削除対象)
-ac, --all-channels 削除対象ファイルのチャンネル指定 (指定しない場合: コマンド実行チャンネルのファイルのみが削除対象)
-au, --all-user 削除対象ファイルをアップロードしたユーザー指定 )指定しない場合: コマンド実行者がアップロードしたファイルのみが削除対象)
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

        private static TimeSpan GetTimeStampTo(List<string> textList)
        {
            string toArgPrefix;
            var defaultTimeSpan = DateTime.UtcNow.AddMonths(-1) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (textList.Contains("--to"))
            {
                toArgPrefix = "--to";
            }
            else if (textList.Contains("-t")) {
                toArgPrefix = "-t";
            }
            else
            {
                return defaultTimeSpan;
            }

            var dateStrIndex = textList.IndexOf(toArgPrefix) + 1;
            if (dateStrIndex >= textList.Count) {
                return defaultTimeSpan;
            }

            var dateStr = textList[dateStrIndex];
            logger.LogInformation(dateStr);
            DateTime dateTime;
            return DateTime.TryParse(dateStr, out dateTime)
                ? DateTime.Parse(dateStr).ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                : defaultTimeSpan;
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

        private static string GetListURI(SlackRequest slackRequest)
        {
            var slackRequestTextList = ToSlackRequestTextList(slackRequest.text);

            var timeStampTo = GetTimeStampTo(slackRequestTextList);
            var uri = $"https://slack.com/api/files.list?token={token}&ts_to={timeStampTo.TotalSeconds.ToString()}";

            var channel = GetTargetChannel(slackRequestTextList, slackRequest.channel_id);
            if (!string.IsNullOrEmpty(channel)) {
                uri += $"&channel={channel}";
                logger.LogInformation(channel);
            }
            var user = GetTargetUser(slackRequestTextList, slackRequest.user_id);
            if (!string.IsNullOrEmpty(user)) {
                uri += $"&user={user}";
                logger.LogInformation(user);
            }
            return uri;
        }

        private static async Task<FileListResponse> ExecuteGetFileListRequestAsync(SlackRequest slackRequest) {
            var fileListRequestURI = GetListURI(slackRequest);
            logger.LogInformation(fileListRequestURI);
            var listHttpResponse = await client.GetAsync(fileListRequestURI);
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