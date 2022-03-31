using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ScratchCheckerFunc
{
    public class ScratchCheckerFunc
    {
        // Storage接続文字列
        string blobAccessKey = GetEnvironment("BlobAccessKey");
        // 金具検出API
        string detectApiEndpoint = GetEnvironment("DetectApiEndpoint");
        // 傷分析API
        string classApiEndpoint = GetEnvironment("ClassApiEndpoint");
        // 検出しきい値
        double detectThreshold = double.Parse(GetEnvironment("DetectProbabilityThreshold"));
        // 判定しきい値
        double okThreshold = double.Parse(GetEnvironment("OkProbabilityThreshold"));

        // 各API呼び出しパラメータ（キー）
        const string KEY = "Prediction-Key";
        // 各API呼び出しパラメータ（値）
        const string KEY_VALUE = "e139bf45b0d64a31b0d85fb7deee13bd";

        static string BoundaryTemplate = "batch_{0}";

        List<DetectInfo> detectInfos;

        ILogger logger = null;

        class DetectInfo
        {
            public Bitmap image;
            public Rectangle rect;
            public double detectProbability;
            public double okProbability;
            public int detectNo;
        }

        [Function("ScratchChecker")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req,
            FunctionContext executionContext)
        {
            logger = executionContext.GetLogger("ScratchChecker");
            logger.LogInformation("api start.");


            // １．パラメータ（画像ファイル名）取得
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            logger.LogInformation("parameter : " + requestBody);
            JObject jObj = JObject.Parse(requestBody);
            string fileName = jObj.Value<string>("fileName");


            // ２．Storageから画像ファイルを取得
            Bitmap railImage = await GetImageAsync(fileName);


            // ３．画像ファイル内の金具位置検出（検出API呼び出し）
            JObject pos = Detect(railImage, fileName);
            logger.LogInformation("検出API呼び出し結果");
            logger.LogInformation(JsonConvert.SerializeObject(pos, Formatting.Indented));

            List<JObject> predictions = ((JArray)pos["predictions"]).ToObject<List<JObject>>();
            

            // ４．検出APIの結果を元に画像切り出し
            detectInfos = CutImage(railImage, ((JArray)pos["predictions"]).ToObject<List<JObject>>());


            // ５．切り出した画像ファイルをStorageに保存
            List<string> fileNames = await SaveImageAsync(railImage.RawFormat, fileName);


            // ６．切り出した画像ファイルの傷検出（分類API呼び出し）
            JArray result = ScratchCheck(railImage.RawFormat, fileNames);
            logger.LogInformation("分類API呼び出し結果");
            logger.LogInformation(JsonConvert.SerializeObject(result, Formatting.Indented));


            // ７．切り出した画像ファイル名、傷検出結果を返す
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            response.WriteString(JsonConvert.SerializeObject(result));

            // ８．矩形を描画した画像をStorageに保存
            Bitmap resultImage = DrawResultImage(railImage, result);
            await SaveResultImageAsync(resultImage, railImage.RawFormat, fileName);


            logger.LogInformation("api end.");
            return response;
        }


        // 環境変数取得
        static private string GetEnvironment(string key)
        {
            return Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);
        }

        // 画像ファイル取得
        private async Task<Bitmap> GetImageAsync(string fileName)
        {
            string connectionString = blobAccessKey;

            BlobServiceClient service = new BlobServiceClient(connectionString);
            //await service.GetPropertiesAsync();
            BlobContainerClient blobContainerClient = service.GetBlobContainerClient("upload");
            BlobClient blobClient = blobContainerClient.GetBlobClient(fileName);

            Bitmap bitmap;
            using (MemoryStream stream = new MemoryStream())
            {
                Azure.Response response = await blobClient.DownloadToAsync(stream).ConfigureAwait(false);
                bitmap = new Bitmap(stream);
            }

            return bitmap;
        }

        // 金具位置検出（検出API呼び出し）
        private JObject Detect(Bitmap image, string fileName)
        {
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, detectApiEndpoint);
            requestMessage.Headers.Add(KEY, KEY_VALUE);

            var boundary = string.Format(BoundaryTemplate, Guid.NewGuid());
            var content = new MultipartFormDataContent(boundary);

            var imageBinary = GetImageBinary(image, image.RawFormat);

            content.Add(new ByteArrayContent(imageBinary), fileName);
            requestMessage.Content = content;

            var httpClient = new HttpClient();

            Task<HttpResponseMessage> httpRequest = httpClient.SendAsync(requestMessage,
                                                                         HttpCompletionOption.ResponseContentRead,
                                                                         CancellationToken.None);
            HttpResponseMessage httpResponse = httpRequest.Result;
            HttpContent responseContent = httpResponse.Content;

            string json = null;

            if (responseContent != null)
            {
                Task<string> stringContentsTask = responseContent.ReadAsStringAsync();
                json = stringContentsTask.Result;
            }

            return JObject.Parse(json);
        }

        // 金具画像切り出し
        private List<DetectInfo> CutImage(Bitmap image, List<JObject> predictions)
        {
            //List<Bitmap> imageList = new List<Bitmap>();
            List<DetectInfo> detectInfos = new List<DetectInfo>();

            int orgWidth = image.Width;
            int orgHeight = image.Height;

            foreach (JObject prediction in predictions)
            {
                double probability = prediction.Value<double>("probability");
                if (probability < detectThreshold) continue;

                JObject boundingBox = prediction.Value<JObject>("boundingBox");

                int left = (int)Math.Floor((orgWidth * boundingBox.Value<double>("left")));
                int top = (int)Math.Floor((orgHeight * boundingBox.Value<double>("top")));
                int width = (int)Math.Floor((orgWidth * boundingBox.Value<double>("width")));
                int height = (int)Math.Floor((orgHeight * boundingBox.Value<double>("height")));

                Bitmap dest = new Bitmap(width, height, image.PixelFormat);

                var g = Graphics.FromImage(dest);
                var dstRect = new Rectangle(0, 0, width, height);
                g.DrawImage(image, dstRect, new Rectangle(left, top, width, height), GraphicsUnit.Pixel);
                g.Dispose();

                //imageList.Add(dest);

                detectInfos.Add(new DetectInfo() {
                    image = dest, 
                    rect = new Rectangle(left, top, width, height),
                    detectProbability = probability,
                });
            }

            return detectInfos;
        }

        // 傷検出（分類API呼び出し）
        private JArray ScratchCheck(ImageFormat imgFormat, List<string> fileNames)
        {
            JArray retArray = new JArray();
            int i = 0;
            foreach (var detectInfo in detectInfos)
            {
                detectInfo.detectNo = i + 1;

                Bitmap image = detectInfo.image;

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, classApiEndpoint);
                requestMessage.Headers.Add(KEY, KEY_VALUE);

                var boundary = string.Format(BoundaryTemplate, Guid.NewGuid());
                var content = new MultipartFormDataContent(boundary);

                var imageBinary = GetImageBinary(image, imgFormat);

                content.Add(new ByteArrayContent(imageBinary), fileNames[i]);
                requestMessage.Content = content;

                var httpClient = new HttpClient();

                Task<HttpResponseMessage> httpRequest = httpClient.SendAsync(requestMessage,
                                                                             HttpCompletionOption.ResponseContentRead,
                                                                             CancellationToken.None);
                HttpResponseMessage httpResponse = httpRequest.Result;
                HttpStatusCode statusCode = httpResponse.StatusCode;
                HttpContent responseContent = httpResponse.Content;

                string json = null;

                if (responseContent != null)
                {
                    Task<string> stringContentsTask = responseContent.ReadAsStringAsync();
                    json = stringContentsTask.Result;
                    logger.LogInformation(json);
                    JObject jObj = JObject.Parse(json);
                    JObject jObjCnv = ConvertResult(jObj, fileNames[i]);
                    retArray.Add(jObjCnv);

                    detectInfo.okProbability = jObjCnv.Value<double>("probabilityOk");

                    i++;
                }
            }

            return retArray;
        }

        private JObject ConvertResult(JObject jObj, string fileName)
        {
            string probabilityOk = "";
            string probabilityNg = "";

            JArray predictions = jObj.Value<JArray>("predictions");
            foreach(JObject obj in predictions)
            {
                string probability = obj.Value<string>("probability");
                string tagName = obj.Value<string>("tagName");
                if ("OK".Equals(tagName.ToUpper()))
                {
                    probabilityOk = probability;
                }
                else
                {
                    probabilityNg = probability;
                }
            }

            JObject retObj = new JObject();
            retObj.Add("fileName", fileName);
            retObj.Add("probabilityOk", probabilityOk);
            retObj.Add("probabilityNg", probabilityNg);

            return retObj;
        }


        // 画像ファイル保存
        private async Task<List<string>> SaveImageAsync(ImageFormat imgFormat, string orgFileName)
        {

            string connectionString = blobAccessKey;

            BlobServiceClient service = new BlobServiceClient(connectionString);
            BlobContainerClient blobContainerClient = service.GetBlobContainerClient("result");

            string[] wk = orgFileName.Split(".");

            int i = 1;
            List<string> fileNames = new List<string>();
            foreach(var detectInfo in detectInfos)
            {
                Bitmap image = detectInfo.image;

                string fileName = wk[0] + "_" + i.ToString() + "." + wk[1];
                BlobClient blobClient = blobContainerClient.GetBlobClient(wk[0] + "/" + fileName);

                using (MemoryStream stream = new MemoryStream(GetImageBinary(image, imgFormat)))
                {
                    await blobClient.UploadAsync(stream, true);
                }
                fileNames.Add(fileName);
                i++;
            }

            return fileNames;
        }

        static byte[] GetImageBinary(Bitmap image, ImageFormat imgFormat)
        {
            using (var ms = new MemoryStream())
            {
                Bitmap bmp = new Bitmap(image);
                bmp.Save(ms, imgFormat);
                return ms.ToArray();
            }
        }

        // 結果画像作成
        private Bitmap DrawResultImage(Bitmap image, JArray classResults)
        {

            int orgWidth = image.Width;
            int orgHeight = image.Height;

            Bitmap resultImage = new Bitmap(orgWidth, orgHeight, image.PixelFormat);
            var g = Graphics.FromImage(resultImage);
            var p1 = new Pen(Color.Red, 2);
            var p2 = new Pen(Color.Blue, 2);

            g.DrawImage(image, 0, 0);

            foreach (var detectInfo in detectInfos)
            {
                //矩形を描画
                if(detectInfo.okProbability >= okThreshold)
                {
                    g.DrawRectangle(p2, detectInfo.rect);
                }
                else
                {
                    g.DrawRectangle(p1, detectInfo.rect);
                }
            }

            p1.Dispose();
            p2.Dispose();
            g.Dispose();

            return resultImage;
        }

        // 結果画像の保存
        private async Task SaveResultImageAsync(Bitmap image, ImageFormat imgFormat, string fileName)
        {
            string connectionString = blobAccessKey;

            BlobServiceClient service = new BlobServiceClient(connectionString);
            BlobContainerClient blobContainerClient = service.GetBlobContainerClient("result");


            BlobClient blobClient = blobContainerClient.GetBlobClient(fileName);

            using (MemoryStream stream = new MemoryStream(GetImageBinary(image, imgFormat)))
            {
                await blobClient.UploadAsync(stream, true);
            }

            return;
        }
    }
}
