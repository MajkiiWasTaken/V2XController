#  V2X Controller

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)
![UI](https://img.shields.io/badge/UI-WPF-512BD4)
![Language](https://img.shields.io/badge/language-C%23-239120)
![Status](https://img.shields.io/badge/status-Prototype%20%2F%20Internal-orange)

---

##  Overview

**V2X Controller** is a WPF desktop application designed for working with **V2X data**, visualizing vehicles on a map, drawing **activation/switch zones**, replaying recordings, and exporting zones to external hardware via **Modbus TCP / serial / serial tunnel**.

The application combines multiple functionalities into a single tool:

- map rendering using OSM tiles,
- receiving and parsing V2X messages over serial communication,
- visualization of live and replayed vehicles,
- drawing and editing map objects,
- saving/loading map configurations to XML,
- exporting zones to MPC devices,
- built-in **Protobuf message decoder**,
- debug terminal for internal diagnostics.

---

##  Features

###  Map Visualization
- tile-based map rendering,
- smooth panning and zooming,
- recalculation of overlay objects on zoom/center change,
- RSU area visualization and GPS-based positioning,
- local altitude estimation for filtering and visualization.

###  Live V2X Data Processing
- connection to COM port,
- configurable baudrate,
- reading incoming XML/V2X messages,
- parsing CAM/SRV messages,
- updating active vehicles in table and map,
- vehicle movement trail visualization.

###  Map Object Drawing
- **Activation Zone mode**,
- **Railway mode**,
- manual tram simulation,
- object selection and editing,
- support for switch zones and standard zones,
- undo/redo stack for drawing actions.

###  Recording & Playback
- recording of live CAM data,
- saving recordings,
- loading playback files,
- keyframe-based playback,
- manual stepping via slider,
- loop and playback speed control,
- timeshift / catch-up logic.

###  Export to MPC
- exporting zones into device registers,
- support for **Modbus TCP**,
- support for **serial communication**,
- support for **serial tunnel mode**,
- reading registers,
- device type validation,
- batch writing of zones into holding registers.

###  Protobuf Decoder
Dedicated `ProtobufWindow` allows:
- loading multiple `.proto` files,
- merging definitions,
- decoding Hex/Base64 input,
- storing proto definitions in AppData,
- quick internal decoding without external tools.

###  Debug Terminal
`TerminalWindow` provides a simple colored terminal for diagnostic logs.

---

##  How It Works

The application is built around a **map-based visualization engine**, where all entities (vehicles, zones, signals) are rendered on a canvas aligned with geographic coordinates.

### Core workflow:

1. **Map Rendering**
   - Tiles are fetched from OpenStreetMap
   - Coordinates are converted from latitude/longitude to pixels
   - Zoom recalculates tiles instead of scaling images

2. **Data Input**
   - Incoming V2X or simulated data
   - Parsed into internal data structures

3. **Object Representation**
   - Vehicles and zones are represented as UI elements
   - Bound to geographic coordinates

4. **User Interaction**
   - Drawing tools allow creation of custom zones
   - Objects can be selected and modified

5. **Real-Time Updates**
   - UI updates dynamically based on incoming or replayed data

---

##  Data Flow

### Live Mode
1. User selects COM port and baudrate  
2. Application opens `SerialPort`  
3. Incoming messages are read  
4. Messages are parsed (CAM/SRV)  
5. Coordinates are converted to map positions  
6. Vehicles are rendered/updated on canvas  
7. UI elements (trails, tables, zones) are updated  

### Playback Mode
1. Recorded file is loaded  
2. Keyframes and replay structures are created  
3. Slider/timer selects current timestamp  
4. Vehicles are interpolated and rendered  
5. UI is synchronized with playback time  

### Export Mode
1. User opens export window  
2. Selects connection type and profile  
3. Zones are divided into activation and switch zones  
4. Target device is validated  
5. Zone data is converted to register values  
6. Batch write is executed via Modbus  

---

##  Requirements

- Windows  
- .NET 8 SDK  
- Visual Studio 2022 or newer  
- External Modbus libraries available  

---

##  Getting Started

1. Clone the repository  
2. Open the project in Visual Studio  
3. Restore NuGet packages  
4. Verify external DLL references  
5. Build and run the application  

>  Some external DLL references may require manual adjustment depending on your environment.

---

##  Technologies Used

- **C# / WPF**
- **.NET**
- **OpenStreetMap tiles**
- **Serial communication**
- **Modbus (TCP / Serial)**
- **Google Protobuf**

---

##  Use Case

This application is intended for:

- development and testing of V2X systems,
- tram/vehicle movement visualization,
- infrastructure diagnostics,
- simulation and validation of transport scenarios.

---

##  Notes

This project is primarily an **internal tool** used for development, testing, and diagnostics.  
It combines HMI, communication, data parsing, and export functionality into a single desktop application.

---

##  Author: Michal Švrček and others

Developed as part of a real-world intelligent transport system project.
