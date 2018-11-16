using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

using Windows.Media.Capture;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using System.Diagnostics;
using Windows.System.Display;
using Windows.Media.MediaProperties;

using Windows.Graphics.Display;
using Windows.Storage.FileProperties;

// used for drawing face rectangle    @pwcasdf
using Windows.Devices.Sensors;
using Windows.Media.Core;
using Windows.UI.Core;
using Windows.Media.FaceAnalysis;
using Windows.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.UI;

using Windows.Media;
using Windows.UI.Xaml.Media.Imaging;

// used for model loading    @pwcasdf
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.AI.MachineLearning;
using System.Collections.Generic;

// to open web page    @pwcasdf
using Windows.System;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace HimOrNot
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        // for camera frame    @pwcasdf
        private MediaCapture mediaCapture;
        private bool isInitialized;
        private bool externalCamera;
        private bool mirroringPreview;
        private readonly DisplayRequest displayRequest = new DisplayRequest();
        private IMediaEncodingProperties previewProperties;
        private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        // for rotating helpers    @pwcasdf
        private SimpleOrientation deviceOrientation = SimpleOrientation.NotRotated;
        private DisplayOrientations displayOrientation = DisplayOrientations.Portrait;
        private readonly DisplayInformation displayInformation = DisplayInformation.GetForCurrentView();
        bool reverseCamera = true;  // set if camera preview needs reverse streaming    @pwcadsf

        // for rectangle around the face    @pwcasdf
        private FaceDetectionEffect faceDetectionEffect;

        // face cropping    @pwcasdf
        VideoFrame croppedFace = null;

        // bringing the onnx model into solution    @pwcasdf
        private ONNXModel model = null;

        // counting darby count    @pwcasdf
        float c = 0;

        public MainPage()
        {
            this.InitializeComponent();
        }

        #region OnLoaded, OnNavigatedTo

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            LoadModelAsync();
            await InitializeCameraAsync();
            await CreateFaceDetectionEffectAsync();
        }

        #endregion

        #region Load model

        private async void LoadModelAsync()
        {
            // Load the .onnx file
            var modelFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri($"ms-appx:///Assets/DarbyOrNot6.onnx"));
            // Create the model from the file
            // IMPORTANT: Change `Model.CreateModel` to match the class and methods in the
            //   .cs file generated from the ONNX model
            model = await ONNXModel.CreateONNXModel(modelFile);
        }

        #endregion

        #region Camera frame work

        private async Task InitializeCameraAsync()
        {
            if (mediaCapture == null)
            {
                var cameraDevice = await FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel.Front);

                mediaCapture = new MediaCapture();

                var settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id };

                // Initialize MediaCapture
                try
                {
                    await mediaCapture.InitializeAsync(settings);
                    isInitialized = true;
                }
                catch (UnauthorizedAccessException)
                {
                    Debug.WriteLine("The app was denied access to the camera");
                }

                // If initialization succeeded, start the preview
                if (isInitialized)
                {
                    // Figure out where the camera is located
                    if (cameraDevice.EnclosureLocation == null || cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown)
                    {
                        // No information on the location of the camera, assume it's an external camera, not integrated on the device
                        externalCamera = true;
                    }
                    else
                    {
                        // Camera is fixed on the device
                        externalCamera = false;

                        // Only mirror the preview if the camera is on the front panel
                        mirroringPreview = (cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
                    }

                    await StartPreviewAsync();
                }
            }
        }

        private static async Task<DeviceInformation> FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel desiredPanel)
        {
            // Get available devices for capturing pictures
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            // Get the desired camera by panel
            DeviceInformation desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

            // If there is no device mounted on the desired panel, return the first device found
            return desiredDevice ?? allVideoDevices.FirstOrDefault();
        }

        private async Task StartPreviewAsync()
        {
            // Prevent the device from sleeping while the preview is running
            displayRequest.RequestActive();

            // Set the preview source in the UI and mirror it if necessary
            PreviewControl.Source = mediaCapture;

            PreviewControl.FlowDirection = reverseCamera ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            //PreviewControl.FlowDirection = FlowDirection.RightToLeft;
            //PreviewControl.FlowDirection = mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.RightToLeft;

            // Start the preview
            await mediaCapture.StartPreviewAsync();
            previewProperties = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

            // Initialize the preview to the current orientation
            if (previewProperties != null)
            {
                displayOrientation = displayInformation.CurrentOrientation;

                await SetPreviewRotationAsync();
            }
        }

        /// <summary>
        /// Gets the current orientation of the UI in relation to the device (when AutoRotationPreferences cannot be honored) and applies a corrective rotation to the preview
        /// </summary>
        private async Task SetPreviewRotationAsync()
        {
            // Only need to update the orientation if the camera is mounted on the device
            //if (externalCamera) return;

            // Calculate which way and how far to rotate the preview
            int rotationDegrees = ConvertDisplayOrientationToDegrees(displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored
            if (mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
            var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
            props.Properties.Add(RotationKey, rotationDegrees);
            await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
        }

        #endregion

        #region Rotaion helpers

        /// <summary>
        /// Calculates the current camera orientation from the device orientation by taking into account whether the camera is external or facing the user
        /// </summary>
        /// <returns>The camera orientation in space, with an inverted rotation in the case the camera is mounted on the device and is facing the user</returns>
        private SimpleOrientation GetCameraOrientation()
        {
            if (externalCamera)
            {
                // Cameras that are not attached to the device do not rotate along with it, so apply no rotation
                return SimpleOrientation.NotRotated;
            }

            var result = deviceOrientation;

            // Account for the fact that, on portrait-first devices, the camera sensor is mounted at a 90 degree offset to the native orientation
            if (displayInformation.NativeOrientation == DisplayOrientations.Portrait)
            {
                switch (result)
                {
                    case SimpleOrientation.Rotated90DegreesCounterclockwise:
                        result = SimpleOrientation.NotRotated;
                        break;
                    case SimpleOrientation.Rotated180DegreesCounterclockwise:
                        result = SimpleOrientation.Rotated90DegreesCounterclockwise;
                        break;
                    case SimpleOrientation.Rotated270DegreesCounterclockwise:
                        result = SimpleOrientation.Rotated180DegreesCounterclockwise;
                        break;
                    case SimpleOrientation.NotRotated:
                        result = SimpleOrientation.Rotated270DegreesCounterclockwise;
                        break;
                }
            }

            // If the preview is being mirrored for a front-facing camera, then the rotation should be inverted
            if (mirroringPreview)
            {
                // This only affects the 90 and 270 degree cases, because rotating 0 and 180 degrees is the same clockwise and counter-clockwise
                switch (result)
                {
                    case SimpleOrientation.Rotated90DegreesCounterclockwise:
                        return SimpleOrientation.Rotated270DegreesCounterclockwise;
                    case SimpleOrientation.Rotated270DegreesCounterclockwise:
                        return SimpleOrientation.Rotated90DegreesCounterclockwise;
                }
            }

            return result;
        }

        /// <summary>
        /// Converts the given orientation of the device in space to the corresponding rotation in degrees
        /// </summary>
        /// <param name="orientation">The orientation of the device in space</param>
        /// <returns>An orientation in degrees</returns>
        private static int ConvertDeviceOrientationToDegrees(SimpleOrientation orientation)
        {
            switch (orientation)
            {
                case SimpleOrientation.Rotated90DegreesCounterclockwise:
                    return 90;
                case SimpleOrientation.Rotated180DegreesCounterclockwise:
                    return 180;
                case SimpleOrientation.Rotated270DegreesCounterclockwise:
                    return 270;
                case SimpleOrientation.NotRotated:
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Converts the given orientation of the app on the screen to the corresponding rotation in degrees
        /// </summary>
        /// <param name="orientation">The orientation of the app on the screen</param>
        /// <returns>An orientation in degrees</returns>
        private static int ConvertDisplayOrientationToDegrees(DisplayOrientations orientation)
        {
            switch (orientation)
            {
                case DisplayOrientations.Portrait:
                    return 90;
                case DisplayOrientations.LandscapeFlipped:
                    return 180;
                case DisplayOrientations.PortraitFlipped:
                    return 270;
                case DisplayOrientations.Landscape:
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Converts the given orientation of the device in space to the metadata that can be added to captured photos
        /// </summary>
        /// <param name="orientation">The orientation of the device in space</param>
        /// <returns></returns>
        private static PhotoOrientation ConvertOrientationToPhotoOrientation(SimpleOrientation orientation)
        {
            switch (orientation)
            {
                case SimpleOrientation.Rotated90DegreesCounterclockwise:
                    return PhotoOrientation.Rotate90;
                case SimpleOrientation.Rotated180DegreesCounterclockwise:
                    return PhotoOrientation.Rotate180;
                case SimpleOrientation.Rotated270DegreesCounterclockwise:
                    return PhotoOrientation.Rotate270;
                case SimpleOrientation.NotRotated:
                default:
                    return PhotoOrientation.Normal;
            }
        }

        /// <summary>
        /// Uses the current display orientation to calculate the rotation transformation to apply to the face detection bounding box canvas
        /// and mirrors it if the preview is being mirrored
        /// </summary>
        private void SetFacesCanvasRotation()
        {
            // Calculate how much to rotate the canvas
            int rotationDegrees = ConvertDisplayOrientationToDegrees(displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored, just like in SetPreviewRotationAsync
            if (mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            // Apply the rotation
            var transform = new RotateTransform { Angle = rotationDegrees };
            FacesCanvas.RenderTransform = transform;

            var previewArea = GetPreviewStreamRectInControl(previewProperties as VideoEncodingProperties, PreviewControl);

            // For portrait mode orientations, swap the width and height of the canvas after the rotation, so the control continues to overlap the preview
            if (displayOrientation == DisplayOrientations.Portrait || displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                FacesCanvas.Width = previewArea.Height;
                FacesCanvas.Height = previewArea.Width;

                // The position of the canvas also needs to be adjusted, as the size adjustment affects the centering of the control
                Canvas.SetLeft(FacesCanvas, previewArea.X - (previewArea.Height - previewArea.Width) / 2);
                Canvas.SetTop(FacesCanvas, previewArea.Y - (previewArea.Width - previewArea.Height) / 2);
            }
            else
            {
                FacesCanvas.Width = previewArea.Width;
                FacesCanvas.Height = previewArea.Height;

                Canvas.SetLeft(FacesCanvas, previewArea.X);
                Canvas.SetTop(FacesCanvas, previewArea.Y);
            }

            // Also mirror the canvas if the preview is being mirrored
            FacesCanvas.FlowDirection = mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }

        #endregion

        #region Face detection helpers

        /// <summary>
        /// Iterates over all detected faces, creating and adding Rectangles to the FacesCanvas as face bounding boxes
        /// </summary>
        /// <param name="faces">The list of detected faces from the FaceDetected event of the effect</param>
        private async void HighlightDetectedFaces(IReadOnlyList<DetectedFace> faces)
        {
            // Remove any existing rectangles from previous events
            FacesCanvas.Children.Clear();

            // For each detected face
            for (int i = 0; i < faces.Count; i++)
            {
                // Face coordinate units are preview resolution pixels, which can be a different scale from our display resolution, so a conversion may be necessary
                Rectangle faceBoundingBox = ConvertPreviewToUiRectangle(faces[i].FaceBox);
                
                // Set bounding box stroke properties
                faceBoundingBox.StrokeThickness = 2;

                // Highlight the first face in the set
                faceBoundingBox.Stroke = (i == 0 ? new SolidColorBrush(Colors.Blue) : new SolidColorBrush(Colors.DeepSkyBlue));

                // model results below the face rectangle    @pwcasdf
                TextBlock modelResult = new TextBlock();
                Canvas.SetLeft(modelResult, Canvas.GetLeft(faceBoundingBox));
                Canvas.SetTop(modelResult, Canvas.GetTop(faceBoundingBox) + faceBoundingBox.ActualHeight);
                modelResult.FontSize = 20;

                // Add grid to canvas containing all face UI objects
                FacesCanvas.Children.Add(faceBoundingBox);
                FacesCanvas.Children.Add(modelResult);

                //showing faces only on right gird    @pwcasdf
                VideoFrame vf = await GetFaceImage(PreviewGrid, Canvas.GetLeft(faceBoundingBox), Canvas.GetTop(faceBoundingBox), faceBoundingBox.ActualWidth, faceBoundingBox.ActualHeight);

                ONNXModelInput modelInput = new ONNXModelInput();
                modelInput.data = vf;

                var evalOutput = await model.EvaluateAsync(modelInput);

                var predictedPersonName = evalOutput.classLabel.GetAsVectorView()[0];
                var score = (evalOutput.loss[0][predictedPersonName] * 100.0f);

                if (predictedPersonName == "Darby")
                {
                    modelResult.Foreground = new SolidColorBrush(Colors.Red);
                    if (c >= 100)
                    {
                        var webpageOpen = await Windows.System.Launcher.LaunchUriAsync(new Uri("https://docs.microsoft.com/en-us/windows/ai/chaining"));
                        Application.Current.Exit();
                        c = 0;
                    }
                    else
                    {
                        c = c + evalOutput.loss[0][predictedPersonName] * 30.00f;
                        if (c > 100)
                        {
                            c = 100;
                        }
                        countTextBlock.Text = c.ToString("#0.00") + " %";
                    }
                }
                else
                {
                    modelResult.Foreground = new SolidColorBrush(Colors.Yellow);
                    c = c - (evalOutput.loss[0][predictedPersonName] * 15.00f);
                    if (c < 0)
                        c = 0;
                    countTextBlock.Text = c.ToString("#0.00") + " %";
                }

                modelResult.Text = predictedPersonName + " " + score.ToString("#0.00");

                //test.Text = "width: " + faceBoundingBox.Width.ToString() + "\nheight: " + faceBoundingBox.Height.ToString() + "\ntop point location:" + Canvas.GetTop(faceBoundingBox) + "\nleft point location: " + Canvas.GetLeft(faceBoundingBox) + "\nActualHeight: " + PreviewControl.ActualHeight.ToString() + "\nActualWidth: " + PreviewControl.ActualWidth.ToString() + "\ntest value: " + FacesCanvas.Width.ToString() + "\ntest value2: " + FacesCanvas.Height.ToString() + "\n";
            }

            // Update the face detection bounding box canvas orientation
            SetFacesCanvasRotation();
        }

        private async void FaceDetectionEffect_FaceDetected(FaceDetectionEffect sender, FaceDetectedEventArgs args)
        {
            // Ask the UI thread to render the face bounding boxes
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HighlightDetectedFaces(args.ResultFrame.DetectedFaces));
        }

        private async Task CreateFaceDetectionEffectAsync()
        {
            // Create the definition, which will contain some initialization settings
            var definition = new FaceDetectionEffectDefinition();

            // To ensure preview smoothness, do not delay incoming samples
            definition.SynchronousDetectionEnabled = false;

            // In this scenario, choose detection speed over accuracy
            definition.DetectionMode = FaceDetectionMode.HighPerformance;

            // Add the effect to the preview stream
            faceDetectionEffect = (FaceDetectionEffect)await mediaCapture.AddVideoEffectAsync(definition, MediaStreamType.VideoPreview);

            // Register for face detection events
            faceDetectionEffect.FaceDetected += FaceDetectionEffect_FaceDetected;

            // Choose the shortest interval between detection events
            faceDetectionEffect.DesiredDetectionInterval = TimeSpan.FromMilliseconds(100);

            // Start detecting faces
            faceDetectionEffect.Enabled = true;
        }

        /// <summary>
        /// Takes face information defined in preview coordinates and returns one in UI coordinates, taking
        /// into account the position and size of the preview control.
        /// </summary>
        /// <param name="faceBoxInPreviewCoordinates">Face coordinates as retried from the FaceBox property of a DetectedFace, in preview coordinates.</param>
        /// <returns>Rectangle in UI (CaptureElement) coordinates, to be used in a Canvas control.</returns>
        private Rectangle ConvertPreviewToUiRectangle(BitmapBounds faceBoxInPreviewCoordinates)
        {
            var result = new Rectangle();
            var previewStream = previewProperties as VideoEncodingProperties;

            // If there is no available information about the preview, return an empty rectangle, as re-scaling to the screen coordinates will be impossible
            if (previewStream == null) return result;

            // Similarly, if any of the dimensions is zero (which would only happen in an error case) return an empty rectangle
            if (previewStream.Width == 0 || previewStream.Height == 0) return result;

            double streamWidth = previewStream.Width;
            double streamHeight = previewStream.Height;

            // For portrait orientations, the width and height need to be swapped
            if (displayOrientation == DisplayOrientations.Portrait || displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamHeight = previewStream.Width;
                streamWidth = previewStream.Height;
            }

            // Get the rectangle that is occupied by the actual video feed
            var previewInUI = GetPreviewStreamRectInControl(previewStream, PreviewControl);

            // Scale the width and height from preview stream coordinates to window coordinates
            result.Width = ((faceBoxInPreviewCoordinates.Width+50) / streamWidth) * previewInUI.Width;
            result.Height = ((faceBoxInPreviewCoordinates.Height+50) / streamHeight) * previewInUI.Height;

            // Scale the X and Y coordinates from preview stream coordinates to window coordinates
            var x = reverseCamera ? FacesCanvas.Width - ((faceBoxInPreviewCoordinates.X - 25) / streamWidth) * previewInUI.Width - result.Width : ((faceBoxInPreviewCoordinates.X - 25) / streamWidth) * previewInUI.Width;
            var y = ((faceBoxInPreviewCoordinates.Y-25) / streamHeight) * previewInUI.Height;
            Canvas.SetLeft(result, x);
            Canvas.SetTop(result, y);

            return result;
        }

        public Rect GetPreviewStreamRectInControl(VideoEncodingProperties previewResolution, CaptureElement previewControl)
        {
            var result = new Rect();

            // In case this function is called before everything is initialized correctly, return an empty result
            if (previewControl == null || previewControl.ActualHeight < 1 || previewControl.ActualWidth < 1 ||
                previewResolution == null || previewResolution.Height == 0 || previewResolution.Width == 0)
            {
                return result;
            }

            var streamWidth = previewResolution.Width;
            var streamHeight = previewResolution.Height;

            // For portrait orientations, the width and height need to be swapped
            if (displayOrientation == DisplayOrientations.Portrait || displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamWidth = previewResolution.Height;
                streamHeight = previewResolution.Width;
            }

            // Start by assuming the preview display area in the control spans the entire width and height both (this is corrected in the next if for the necessary dimension)
            result.Width = previewControl.ActualWidth;
            result.Height = previewControl.ActualHeight;

            // If UI is "wider" than preview, letterboxing will be on the sides
            if ((previewControl.ActualWidth / previewControl.ActualHeight > streamWidth / (double)streamHeight))
            {
                var scale = previewControl.ActualHeight / streamHeight;
                var scaledWidth = streamWidth * scale;

                result.X = (previewControl.ActualWidth - scaledWidth) / 2.0;
                result.Width = scaledWidth;
            }
            else // Preview stream is "wider" than UI, so letterboxing will be on the top+bottom
            {
                var scale = previewControl.ActualWidth / streamWidth;
                var scaledHeight = streamHeight * scale;

                result.Y = (previewControl.ActualHeight - scaledHeight) / 2.0;
                result.Height = scaledHeight;
            }

            return result;
        }

        #endregion


        #region cropping faces

        public async Task<VideoFrame> GetFaceImage(Grid grid, double x, double y, double width, double height)
        {
            RenderTargetBitmap renderBitmap = new RenderTargetBitmap();

            await renderBitmap.RenderAsync(grid);
            var buffer = await renderBitmap.GetPixelsAsync();
            var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, renderBitmap.PixelWidth, renderBitmap.PixelHeight, BitmapAlphaMode.Premultiplied);

            buffer = null;
            renderBitmap = null;

            VideoFrame vf = VideoFrame.CreateWithSoftwareBitmap(softwareBitmap);

            try
            {
                await CropAndDisplayInputImageAsync(vf, x, y, width, height);
            }
            catch(Exception e)
            {

            }

            return croppedFace;
        }

        private async Task CropAndDisplayInputImageAsync(VideoFrame inputVideoFrame, double x, double y, double width, double height)
        {
            bool useDX = inputVideoFrame.SoftwareBitmap == null;

            BitmapBounds cropBounds = new BitmapBounds();
            uint h = (uint)FacesCanvas.Height;
            uint w = (uint)FacesCanvas.Width;
            var frameHeight = useDX ? inputVideoFrame.Direct3DSurface.Description.Height : inputVideoFrame.SoftwareBitmap.PixelHeight;
            var frameWidth = useDX ? inputVideoFrame.Direct3DSurface.Description.Width : inputVideoFrame.SoftwareBitmap.PixelWidth;

            // calculate the ratio between original frame in buffer and the screen    @pwcasdf

            double widthRatio = frameWidth / PreviewControl.ActualWidth;
            double blackAreaWidth = (PreviewControl.ActualWidth - FacesCanvas.Width) / 2;

            double heightRatio = frameHeight / PreviewControl.ActualHeight;
            double blackAreaHeight = (PreviewControl.ActualHeight - FacesCanvas.Height) / 2;

            width = width * widthRatio;
            height = height * heightRatio;

            // depends on camera, the showing way is diff, above one is for the laptop cam and below one is for the attached camera device    @pwcasdf
            cropBounds.X = (uint)((blackAreaWidth * widthRatio) + (x * widthRatio));
            cropBounds.Y = (uint)((y * heightRatio) + (blackAreaHeight * heightRatio));
            cropBounds.Width = (uint)width;
            cropBounds.Height = (uint)height;


            croppedFace = new VideoFrame(BitmapPixelFormat.Bgra8, 227, 227, BitmapAlphaMode.Premultiplied);

            await inputVideoFrame.CopyToAsync(croppedFace, cropBounds, null);

            SoftwareBitmap asdf = croppedFace.SoftwareBitmap;
            asdf = SoftwareBitmap.Convert(asdf, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var qwer = new SoftwareBitmapSource();
            await qwer.SetBitmapAsync(asdf);
            CroppedFaceImage.Source = qwer;



            //test2.Text = "left: " + cropBounds.X.ToString() + "\ntop: " + x.ToString() + "\nframewidth: " + frameWidth.ToString() + "\nframeheight: " + frameHeight.ToString() + "\nbox width: " + width.ToString() + "\nbox height: " + height.ToString() + "\nactual width: " + PreviewControl.ActualWidth.ToString() + "\nactual height: " + PreviewControl.ActualHeight.ToString();

            
            //return evalOutput.ToString();
        }

        #endregion
    }
}
