using CommonServiceLocator;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Ioc;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace FileUploadSample
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }
    }

    public class ViewModelLocator
    {
        public ViewModelLocator()
        {
            ServiceLocator.SetLocatorProvider(() => SimpleIoc.Default);

            SimpleIoc.Default.Register<MainViewModel>();
        }

        public MainViewModel Main
            => SimpleIoc.Default.GetInstance<MainViewModel>();
    }

    public class MainViewModel : ViewModelBase
    {
        private const string AADClientId = "36e06055-b160-4a48-a998-8835dccca7d2";
        private const string GraphAPIEndpointPrefix = "https://graph.microsoft.com/v1.0/";
        private string[] AADScopes = new string[] { "files.readwrite.all" };
        private IPublicClientApplication AADAppContext = null;
        private GraphServiceClient graphClient = null;
        private AuthenticationResult _userCredentials;

        private bool _disabled = true;
        public bool Disabled
        {
            get => _disabled;
            set
            {
                if (value != _disabled)
                {
                    _disabled = value;
                    RaisePropertyChanged(nameof(Disabled));

                    UploadFileCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    RaisePropertyChanged(nameof(StatusText));
                }
            }
        }

        private bool _isUploadToSharePoint = false;
        public bool IsUploadToSharePoint
        {
            get => _isUploadToSharePoint;
            set
            {
                if (value != _isUploadToSharePoint)
                {
                    _isUploadToSharePoint = value;
                    RaisePropertyChanged(nameof(IsUploadToSharePoint));
                }
            }
        }

        private RelayCommand<string> _uploadFileCommand;
        public RelayCommand<string> UploadFileCommand => _uploadFileCommand ?? (_uploadFileCommand = new RelayCommand<string>(async args =>
        {
            Disabled = false;
            if (this._userCredentials == null)
            {
                await SignInUser();
            }

            DriveItem uploadedFile = null;

            var (fileName, fileStream) = await PickFile();
            var button = args;
            if (button == "small")
            {
                uploadedFile = await UploadSmallFile(fileName, fileStream);
            }
            else
            {
                uploadedFile = await UploadLargeFile(fileName, fileStream);
            }
            if (uploadedFile != null)
            {
                StatusText = "Uploaded file: " + uploadedFile.Name;
            }
            else
            {
                StatusText = "Upload failed";
            }

            Disabled = true;
        }, _ => Disabled));

        private void InitializeGraph()
        {
            if (_userCredentials != null)
            {
                graphClient = new GraphServiceClient(
                    GraphAPIEndpointPrefix,
                    new DelegateAuthenticationProvider(
                        async (requestMessage) =>
                        {
                            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("bearer", _userCredentials.AccessToken);
                        }
                    )
                );
            }
        }

        /// <summary>
        /// Log the user in to either O365 or OneDrive consumer
        /// </summary>
        /// <returns>A task to await on</returns>
        private async Task<string> SignInUser()
        {
            string status = "Unknown";

            // Instantiate the app with AAD
            AADAppContext = PublicClientApplicationBuilder
                .Create(AADClientId)
                .WithRedirectUri("http://localhost")
                .Build();// new Microsoft.Identity.Client.PublicClientApplication(AADClientId);

            // Get the token, if it fails print out an error message, if it succeeds print out the logged in User's identity as a verification
            try
            {
                _userCredentials = await AADAppContext
                    .AcquireTokenInteractive(AADScopes)
                    .ExecuteAsync();
                if (_userCredentials != null)
                {
                    status = "Signed in as " + _userCredentials.Account.Username;
                    InitializeGraph();
                }
            }
            catch (MsalServiceException serviceEx)
            {
                status = $"Could not sign in, error code: " + serviceEx.ErrorCode;
            }
            catch (Exception ex)
            {
                status = $"Error Acquiring Token: {ex}";
            }

            return (status);
        }

        /// <summary>
        /// Take a file less than 4MB and upload it to the service
        /// </summary>
        /// <param name="fileToUpload">The file that we want to upload</param>
        /// <param name="uploadToSharePoint">Should we upload to SharePoint or OneDrive?</param>
        private async Task<DriveItem> UploadSmallFile(string fileName, Stream fileStream)
        {
            DriveItem uploadedFile = null;

            // Do we want OneDrive for Business/Consumer or do we want a SharePoint Site?
            if (IsUploadToSharePoint)
            {
                uploadedFile = await graphClient.Sites["root"].Drive.Root.ItemWithPath(fileName).Content.Request().PutAsync<DriveItem>(fileStream);
            }
            else
            {
                uploadedFile = await graphClient.Me.Drive.Root.ItemWithPath(fileName).Content.Request().PutAsync<DriveItem>(fileStream);
            }

            return (uploadedFile);
        }

        /// <summary>
        /// Take a file greater than 4MB and upload it to the service
        /// </summary>
        /// <param name="fileToUpload">The file that we want to upload</param>
        /// <param name="uploadToSharePoint">Should we upload to SharePoint or OneDrive?</param>
        private async Task<DriveItem> UploadLargeFile(string fileName, Stream fileStream)
        {
            DriveItem uploadedFile = null;
            UploadSession uploadSession = null;

            // Do we want OneDrive for Business/Consumer or do we want a SharePoint Site?
            if (IsUploadToSharePoint)
            {
                uploadSession = await graphClient.Sites["root"].Drive.Root.ItemWithPath(fileName).CreateUploadSession().Request().PostAsync();
            }
            else
            {
                uploadSession = await graphClient.Me.Drive.Root.ItemWithPath(fileName).CreateUploadSession().Request().PostAsync();
            }

            if (uploadSession != null)
            {
                // Chunk size must be divisible by 320KiB, our chunk size will be slightly more than 1MB
                int maxSizeChunk = (320 * 1024) * 4;
                ChunkedUploadProvider uploadProvider = new ChunkedUploadProvider(uploadSession, graphClient, fileStream, maxSizeChunk);
                var chunkRequests = uploadProvider.GetUploadChunkRequests();
                var exceptions = new List<Exception>();
                //var readBuffer = new byte[maxSizeChunk];
                foreach (var request in chunkRequests)
                {
                    var result = await uploadProvider.GetChunkRequestResponseAsync(request, exceptions);

                    if (result.UploadSucceeded)
                    {
                        uploadedFile = result.ItemResponse;
                    }
                }
            }

            return (uploadedFile);
        }

        private async Task<(string path, Stream stream)> PickFile()
        {
            var picker = new OpenFileDialog();
            picker.Filter = "图像文件(*.bmp, *.jpg)|*.bmp;*.jpg|所有文件|*.*";
            picker.Multiselect = false;
            if (picker.ShowDialog() == true)
            {
                var path = picker.FileName;

                var fileName = path.Split(@"\").LastOrDefault();
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read);
                var stream = new MemoryStream();
                await reader.CopyToAsync(stream);
                stream.Seek(0, SeekOrigin.Begin);
                return (fileName, stream);
            }
            return (string.Empty, null);
        }

    }
}
