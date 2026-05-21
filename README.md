# nano-sight

nano-sight is an MR app for Meta Quest 3 that detects faces from the Passthrough Camera on-device, identifies them against a local InsightFace server, and anchors a 3D label on top of each real face.

The app uses YuNet for on-device detection in Unity Inference Engine, then sends new face crops to a server for identification. The result is a face box plus a floating panel with the detected name and confidence (and whatever data you might have).

<img width="724" height="543" alt="BAE41666-EF77-4EBB-B9F2-FFA5F2ECC40D" src="https://github.com/user-attachments/assets/18dbe0c7-7130-4e06-b09d-b2d9738cd458" />


## Requirements

You need a Meta Quest 3, 3s or Pro with the latest Horizon OS.

You also need a reachable face identification server that can receive a cropped face image and return a JSON response with at least `name` and `confidence`.

## Quickstart

Sideload a freshly built apk from the [releases page](../../releases/latest)

Open the app menu with the menu button in your left controller or by tapping together your thumb and index finger in your left hand, adjust the server url and the detection parameters based on your enviroment.

The project is designed so that face detection runs locally on the headset, while identification is handled by your own server.

## Configuring the server

The main setup step is the server url inside the menu options.
Example:
```txt
http://192.168.1.50:8000/identify
```

The request is a POST multipart form upload with aface crop in the image field.
Expected response:
```json
{
  "name": "John Doe",
  "confidence": 0.97
}
```

## How it works

On each capture interval, NanoSight grabs a frame from the Passthrough Camera, runs YuNet to detect faces, tracks them by IoU, and only sends a face to the server when it appears as a new track.
The app then places a 3D face box and a floating label using the camera pose, the face rectangle, and the Quest depth path when available.
If the server returns an unknown face or a confidence below the display threshold, the label is hidden and only the tracking box remains visible.

## Notes

If labels or boxes appear misaligned, check the face detection flip setting.
If the app feels slow, increase the detection interval or switch the detector backend.

## About
Face recognition over AR with Meta Quest 3 glasses
