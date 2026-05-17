# NanoSight · FaceID

App MR para Meta Quest 3 que detecta caras en el feed de la Passthrough Camera **on-device**
(modelo YuNet en Unity Inference Engine), las identifica contra un servidor InsightFace propio,
y ancla en 3D sobre cada cara real: un cuadrado de tracking + un panel flotante con nombre y
confianza.

Proyecto derivado de **Unity-PassthroughCameraApiSamples** (Unity 6, Meta XR SDK). Se recortaron
los samples que no se usan; solo se conservan `CameraToWorld` y el core de `PassthroughCamera`.

---

## 1. Qué se ha añadido / modificado

### `Assets/FaceID/`
| Archivo | Rol |
|---|---|
| `Scripts/FaceIdentifier.cs` | Orquestador. Cada N s captura un frame, lo pasa a YuNet, alimenta el tracker, recorta caras nuevas y las envía al servidor, y coloca labels + cajas en 3D. También crea el HUD de estado. |
| `Scripts/YuNetFaceDetector.cs` | Detector de caras on-device. Envuelve el modelo **YuNet** (ONNX, ~75k params) en Unity Inference Engine: inferencia + decode multi-stride + NMS. Devuelve `Rect` normalizados. |
| `Scripts/FaceServerClient.cs` | Cliente HTTP aislado. POST multipart del JPEG, timeout, cancelación, parseo de `{name, confidence}`. |
| `Scripts/FaceTracker.cs` | Tracker ligero por IoU. Id estable por cara entre frames; eventos `TrackCreated` / `TrackLost`. |
| `Scripts/FaceLabel.cs` | Panel flotante: `Show()`/`Hide()` con fade, billboard, posición lerpeada con offset. |
| `Scripts/FaceLabelManager.cs` | Pool de labels, mapping `trackId → label`, dedup, limpieza de caras desaparecidas. |
| `Scripts/FaceBoxMarker.cs` | Cuadrado 3D (`LineRenderer`) que se pega a la cara y la sigue suavemente. Lo crea `FaceIdentifier` por código, sin prefab. |
| `Scenes/FaceID.unity` | Escena de trabajo (duplicado de `CameraToWorld.unity`). |
| `Prefabs/FaceLabel.prefab` | Canvas World Space 30×20 cm, fondo redondeado, dos `TextMeshProUGUI`, script `FaceLabel` attacheado. |
| `Models/yunet.onnx` | Modelo YuNet (OpenCV Model Zoo, `face_detection_yunet_2023mar`, MIT). |

### Modificado
- `Assets/Plugins/Android/AndroidManifest.xml` — añadido `android.permission.INTERNET`.
- `ProjectSettings/EditorBuildSettings.asset` — escena de build = solo `FaceID.unity`.
- `ProjectSettings/ProjectSettings.asset` — rellenado `microphoneUsageDescription` (lo exige el SDK de Meta al buildear).

### Sample original
Del fork `Unity-PassthroughCameraApiSamples` solo queda
`Assets/PassthroughCameraApiSamples/PassthroughCamera/Prefabs/PassthroughCameraAccessPrefab.prefab`
(la rig de cámara passthrough que usa la escena) y el `LICENSE.txt` por atribución. Todo lo demás
del sample (CameraToWorld, scripts, materiales, demás prefabs) está borrado.

---

## 2. Requisitos

- **Unity 6** (`6000.4.6f1`).
- Meta Quest 3 / 3S con **Passthrough Camera Access** habilitado, HorizonOS v74+.
- Para la profundidad real: **OpenXR + AR Foundation + Meta Quest: Occlusion** (Meta migró
  Environment Depth a la ruta OpenXR/AR Foundation; el path legacy OVRPlugin ya no la expone).
  Los paquetes necesarios (`com.unity.xr.openxr`, `com.unity.xr.meta-openxr`,
  `com.unity.xr.arfoundation` — vienen transitive con meta-openxr) están en el `manifest.json`.
- Un servidor de identificación accesible desde la red del Quest (ver §5).

---

## 3. Pasos manuales en el Editor

> `FaceID.unity` es un duplicado de `CameraToWorld.unity`: trae la rig de cámara y el
> `PassthroughCameraAccess` funcionando. El resto hay que montarlo a mano.

### 3.1. Importar TextMeshPro Essentials
`Window ▸ TextMeshPro ▸ Import TMP Essential Resources` — el prefab `FaceLabel` usa la fuente
por defecto. Sin esto, el texto de los labels no se renderiza (el panel oscuro sí).

### 3.2. Limpiar la escena (si abres por primera vez después de la limpieza del repo)
Tras borrar los assets zombies del sample, Unity puede dejar 2 GameObjects con "Missing Prefab"
en el Hierarchy de `FaceID.unity`: `CameraToWorldCameraCanvas` y `CameraToWorldButtonA_Highlight`.
Bórralos. **Conserva** todo lo que empiece por `[BuildingBlock]`, el `Directional Light`,
`Origin`, `Metadata` y los GameObjects de FaceID.

### 3.3. GameObjects de FaceID en la escena
Crea estos GameObjects vacíos en la **raíz** de la escena (Hierarchy ▸ click derecho ▸ Create
Empty) y añádeles su componente (Inspector ▸ Add Component):

| GameObject | Componente |
|---|---|
| `FaceLabelManager` | `Face Label Manager` |
| `FaceIdentifier` | `Face Identifier` |
| `EnvironmentRaycast` | `Environment Raycast Manager` *(necesario para profundidad real del Quest)* |

Y este **obligatoriamente** (sin él, el subsistema de Occlusion de OpenXR no arranca y la
profundidad no funciona): `GameObject ▸ XR ▸ AR Session`. Eso crea un GameObject `AR Session`
con el componente `ARSession`. Déjalo en la raíz. No hace falta tocar nada de sus parámetros.

Ya **no** hace falta un GameObject `FaceDetection` — el detector YuNet vive como un campo
dentro de `FaceIdentifier`.

### 3.4. Cablear las referencias (ver §4) y la URL del servidor (§5)

### 3.5. Build & Run
Plataforma Android, Quest conectado, **Build And Run**.

---

## 4. Referencias del Inspector

### `FaceIdentifier`
| Campo | Valor |
|---|---|
| Camera Access | el componente `PassthroughCameraAccess` (en el `[BuildingBlock] Passthrough` / prefab de la escena) |
| Label Manager | el componente `FaceLabelManager` |
| Environment Raycast | el componente `EnvironmentRaycastManager` *(vacío → cae al cálculo de distancia por tamaño de bbox)* |
| **Face Detector (YuNet)** ▸ Model Asset | `Assets/FaceID/Models/yunet.onnx` |
| Face Detector ▸ Score Threshold | `0.6` (sube si hay falsos positivos) |
| Face Detector ▸ Nms Threshold | `0.3` |
| Face Detector ▸ Backend | `GPUCompute` (cambia a `CPU` si hay problemas de GPU) |
| Detection Interval | `0.1` |
| Min Normalized Box Size | `0.04` |
| Flip Detection Y | activado *(si los labels salen invertidos en vertical, desactívalo)* |
| Average Face Width Meters / Fallback / Min / Max Distance | `0.16` / `1.5` / `0.3` / `6` |
| Min Confidence To Show | `0` |
| Show Face Boxes / Face Box Color / Line Width | activado / verde / `0.006` |
| Show Status Hud | activado |
| Verbose Placement Log | desactivado *(actívalo para depurar colocación/profundidad — ver §7)* |
| **Server Client** (desplegable) | ver §5 |
| **Tracker** (desplegable) | `IoU Match Threshold` `0.3`, `Lost Grace Seconds` `1.0` |

### `FaceLabelManager`
| Campo | Valor |
|---|---|
| Label Prefab | `Assets/FaceID/Prefabs/FaceLabel.prefab` |
| Initial Pool Size | `8` |
| Label Parent | *(vacío → raíz de la escena)* |
| Camera Transform | `CenterEyeAnchor` (en `[BuildingBlock] Camera Rig ▸ TrackingSpace`) |
| Hide After Unseen Seconds | `2` |

---

## 5. Configurar el servidor

En el Inspector de `FaceIdentifier`, despliega **Server Client**:

| Campo | Descripción |
|---|---|
| `Server Url` | URL del endpoint, p. ej. `http://192.168.1.50:8000/identify`. **Debe ser la IP de red local** del servidor (no `127.0.0.1` — desde el Quest eso es el propio Quest). El servidor tiene que escuchar en `0.0.0.0`. |
| `Image Field Name` | Campo multipart con el JPEG. Por defecto `image`. |
| `Timeout Seconds` | Timeout de la petición. |
| `Verbose Logging` | Loguea cada petición/respuesta. |

**Contrato HTTP:** `POST` multipart con el recorte de la cara en JPEG (campo `image`).
Respuesta `200 OK`, JSON `{ "name": "...", "confidence": 0.0-1.0 }` (nombres exactos —
`JsonUtility` es sensible a mayúsculas).

---

## 6. Cómo funciona el pipeline

```
PassthroughCameraAccess ─(GetColors)→ Texture2D ─→ YuNetFaceDetector.Detect()  → List<Rect>
        │  (intrínsecos + pose cacheada)                                            │
        ▼                                                                           ▼
ViewportPointToRay + EnvironmentRaycastManager (profundidad real)        FaceTracker (IoU)
        │                                                          → TrackCreated / TrackLost
        ▼                                  (cara nueva) │                            │
   posición 3D real          FaceServerClient.IdentifyAsync(jpeg) → {name, confidence} │
        │                                               │                            │
        └──────► FaceLabelManager (label) + FaceBoxMarker (cuadrado 3D) ◄──────────────┘
```

- **Detección on-device** con YuNet — sin servidor para las cajas.
- El tracker consulta el servidor **una vez por aparición** de cada cara, no por frame.
- La pose de cámara se cachea al capturar: la detección es async, así que la reproyección
  2D→3D usa la pose de ese frame.
- La distancia usa la **profundidad real** del Quest (`EnvironmentRaycastManager`); si no hay
  raycast, cae a una estimación por el tamaño aparente de la cara.

---

## 7. Notas y troubleshooting

- **Labels/cajas mal colocados o invertidos** → activa `Verbose Placement Log` en `FaceIdentifier`,
  reproduce, y mira `adb logcat | grep FaceIdentifier`: imprime la cadena
  bbox→viewport→raycast→distancia. Si salen invertidos en vertical, prueba a desactivar
  `Flip Detection Y`. Si la profundidad es errática, revisa que `EnvironmentRaycastManager` esté
  asignado y soportado (necesita la Depth API del Quest).
- **No detecta nada / cajas raras** → YuNet espera entrada BGR 0-255; el decode asume
  `score = √(cls·obj)` y `size = exp(reg)·stride`. Si las cajas salen con tamaño raro, el factor
  `exp` del decode en `YuNetFaceDetector.DecodeStride` es el primer sospechoso. Ajusta
  `Score Threshold` si hay demasiados/pocos positivos.
- **Texto de los labels o del HUD no se ve** → falta importar TMP Essentials (§3.1). Tanto los
  labels de cara como el chip de estado del HUD usan TextMeshPro, así que sin Essentials no se
  renderiza ninguno.
- **Va lento** → sube el `Detection Interval`, o cambia el `Backend` del detector a `CPU`/`GPUCompute`
  para ver cuál rinde mejor en tu dispositivo.
