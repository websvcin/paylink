using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Drive.v3.Data;

namespace PAYLINK.Services
{
    public class GoogleDriveService
    {
        private readonly DriveService _driveService;

        public GoogleDriveService(string credentialsFilePath)
        {
            GoogleCredential credential;
            using (var stream = new FileStream(credentialsFilePath, FileMode.Open, FileAccess.Read))
            {
                credential = GoogleCredential.FromStream(stream)
                             .CreateScoped(DriveService.Scope.Drive);
            }

            _driveService = new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "PaylinkUploaderApp"
            });
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType)
        {
            var fileMetadata = new Google.Apis.Drive.v3.Data.File()
            {
                Name = fileName,
                Parents = new List<string> { "1BmCI10YO_VJTcj9DwGHokvFP6uey3rJr" } // 👈 Replace this
            };

            var request = _driveService.Files.Create(fileMetadata, fileStream, contentType);
            request.Fields = "id";
            await request.UploadAsync();

            var file = request.ResponseBody;

            // Make the file public
            var permission = new Permission
            {
                Role = "reader",
                Type = "anyone"
            };
            await _driveService.Permissions.Create(permission, file.Id).ExecuteAsync();

            return $"https://drive.google.com/uc?id={file.Id}";
        }

        public async Task ListFilesAsync()
        {
            var request = _driveService.Files.List();
            request.Fields = "files(id, name, mimeType)";
            var result = await request.ExecuteAsync();

            foreach (var file in result.Files)
            {
                Console.WriteLine($"{file.Name} ({file.Id}) - {file.MimeType}");
            }
        }

    }
}
