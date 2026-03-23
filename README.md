# V2X Controller

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)
![UI](https://img.shields.io/badge/UI-WPF-512BD4)
![Language](https://img.shields.io/badge/language-C%23-239120)
![Status](https://img.shields.io/badge/status-Prototype%20%2F%20Internal-orange)

Desktop WPF aplikace pro práci s **V2X daty**, vizualizaci vozidel na mapě, kreslení **aktivních zón / switch zón**, přehrávání záznamů a export zón do externího zařízení přes **Modbus TCP / serial / serial tunnel**.

Projekt kombinuje několik částí do jedné aplikace:

- zobrazení mapy nad OSM dlaždicemi,
- příjem a parsování V2X zpráv přes sériový port,
- zobrazení živých i replay vozidel,
- kreslení a editaci mapových objektů,
- ukládání/načítání mapy do XML,
- export zón do MPC zařízení,
- pomocné okno pro dekódování **Protobuf** zpráv,
- debug terminál pro sledování dění v aplikaci.

---

## Co aplikace umí

### 1. Mapové zobrazení
- vykreslení mapy z dlaždic,
- plynulé posouvání a zoom,
- přepočet překryvných objektů při změně středu nebo zoomu,
- zobrazení RSU oblasti a práce s GPS souřadnicemi,
- dopočet lokální nadmořské výšky pro filtraci a vizualizaci.

### 2. Live příjem V2X zpráv
- připojení na COM port,
- nastavení baudrate,
- čtení příchozích XML/V2X zpráv,
- parsování CAM/SRV dat,
- aktualizace aktivních vozidel v tabulce i na mapě,
- zobrazování trailu pohybu vozidel.

### 3. Kreslení objektů nad mapou
- **Activation Zone** režim,
- **Railway** režim,
- ruční simulace tramvají,
- výběr a úpravy objektů,
- práce se switch zónami a běžnými zónami,
- undo/redo zásobník pro akce nad kreslením.

### 4. Záznam a playback
- záznam live CAM dat,
- ukládání záznamů,
- načtení playback souboru,
- přehrávání po keyframech,
- ruční krokování přes slider,
- loop a změna rychlosti přehrávání,
- timeshift/catch-up logika.

### 5. Export do MPC
- export zón do registrů zařízení,
- podpora **Modbus TCP**,
- podpora **serial port** komunikace,
- podpora **serial tunnel** režimu,
- čtení registrů,
- validace typu zařízení před exportem,
- dávkový zápis zón do holding registrů.

### 6. Protobuf decoder
Samostatné okno `ProtobufWindow` umí:
- načítat více `.proto` souborů,
- spojit definice do jednoho pohledu,
- zkusit dekódovat Hex/Base64 vstup,
- uložit seznam použitých proto definic do AppData,
- dělat rychlý interní test dekódování bez nutnosti externího nástroje.

### 7. Debug terminal
Okno `TerminalWindow` slouží jako jednoduchý barevný terminál pro diagnostické výpisy.

---

## Architektura projektu

Aplikace je postavená jako **single WPF desktop app**, kde `MainWindow` funguje jako hlavní orchestrátor mapy, komunikace a UI logiky.

### Hlavní části

#### `MainWindow.xaml` / `MainWindow.xaml.cs`
Hlavní okno aplikace. Řeší hlavně:
- mapové dlaždice,
- práci s Canvas vrstvou,
- live komunikaci přes `SerialPort`,
- příjem a parsování zpráv,
- vykreslování vozidel,
- aktivní a switch zóny,
- playback a recording,
- XML import/export mapy,
- ovládání většiny UI.

#### `ExportWindow.xaml` / `ExportWindow.xaml.cs`
Samostatné exportní okno pro zápis zón do MPC zařízení.
Obsahuje logiku pro:
- volbu komunikačního profilu,
- Modbus TCP / serial / serial tunnel,
- ukládání export profilů,
- serializaci nastavení,
- zápis zón do registrů,
- čtení registrů,
- progress overlay a diagnostiku exportu.

#### `ProtobufWindow.xaml` / `ProtobufWindow.xaml.cs`
Pomocné okno pro správu `.proto` souborů a ruční dekódování payloadů.

#### `ProtobufParser.cs`
Vlastní interní parser a dekodér protobuf zpráv. Obsahuje:
- parsování `.proto` definic,
- detekci typu zprávy,
- dekódování hex/base64 vstupu,
- převod do lidsky čitelné podoby.

#### `V2XMessageParser.cs`
Parser V2X zpráv z raw XML do interního modelu `V2XMessage`.

#### `SRVMessage.cs`
Model a parser pro SRV zprávu.

#### `ActivationZone.cs`
Datový model zóny s podporou `INotifyPropertyChanged`. Uchovává například:
- název,
- šířku/výšku,
- lat/lon,
- azimut,
- main/sub zone index,
- stav aktivace,
- informaci, zda jde o switch zónu.

#### Další modely
- `MapPoint.cs` – vozidlo / bod na mapě včetně vizuálních prvků a trailu,
- `MapRectangle.cs` – základní obdélník na mapě,
- `Railway.cs` – úsečka koleje,
- `MovementFrame.cs` – jeden frame pohybu pro replay,
- `TramInfo.cs` – data pro tabulku aktivních tramvají,
- `ExportSettings.cs` + `ExportSettingsStorage.cs` – uložení profilů exportu.

---

## Jak data tečou aplikací

### Live režim
1. Uživatel zvolí COM port a baudrate.
2. `MainWindow` otevře `SerialPort`.
3. Aplikace čte příchozí řádky / zprávy.
4. `V2XMessageParser` nebo `SRVMessage` zprávu rozparsuje.
5. Souřadnice se převedou do mapových pixelů.
6. Vozidlo se vykreslí / aktualizuje na Canvasu.
7. Trail, tabulka aktivních tramvají a zóny se přepočítají.

### Playback režim
1. Načte se uložený záznam.
2. Vytvoří se keyframy a interní replay struktury.
3. Slider nebo timer vybírá aktuální čas.
4. Vozidla se dopočtou a vykreslí do mapy.
5. Statistiky a doprovodné UI se synchronizují s časem přehrávání.

### Export režim
1. Uživatel otevře exportní okno.
2. Vybere typ spojení a cílový profil.
3. Zóny se rozdělí na **WLC activation zones** a **RTV switch zones**.
4. Aplikace ověří typ cílového zařízení.
5. Hodnoty zón se přepočtou do registrů.
6. Proběhne dávkový zápis přes Modbus.

---

## Struktura důležitých souborů

```text
V2XController/
├─ App.xaml
├─ MainWindow.xaml
├─ MainWindow.xaml.cs
├─ ExportWindow.xaml
├─ ExportWindow.xaml.cs
├─ ProtobufWindow.xaml
├─ ProtobufWindow.xaml.cs
├─ TerminalWindow.xaml
├─ TerminalWindow.xaml.cs
├─ ActivationZone.cs
├─ V2XMessage.cs
├─ V2XMessageParser.cs
├─ SRVMessage.cs
├─ ProtobufParser.cs
├─ TramInfo.cs
├─ MapPoint.cs
├─ MapRectangle.cs
├─ Railway.cs
├─ MovementFrame.cs
├─ ExportSettings.cs
├─ ExportSettingsStorage.cs
├─ Converters.cs
└─ Libs/
```

---

## Technologie a závislosti

### NuGet balíčky
- `Google.Protobuf`
- `System.IO.Ports`
- `Uno.UI`

### Externí DLL reference
Projekt používá i tyto reference:
- `ComCommon.dll`
- `Common.dll`
- `Logger.dll`
- `ModbusNewLib.dll`

> Pozor: v `.csproj` jsou `HintPath` aktuálně nastavené na relativní cestu do `Downloads/ModbusLib`. Pokud chceš projekt přesunout nebo buildit jinde, bude pravděpodobně potřeba reference upravit tak, aby mířily na lokální `Libs/` složku nebo na správnou firemní cestu.

---

## Build / spuštění

### Požadavky
- Windows
- .NET 8 SDK
- Visual Studio 2022 nebo novější
- dostupné DLL knihovny pro Modbus část

### Doporučený postup
1. Otevři `V2XController.csproj` ve Visual Studiu.
2. Zkontroluj, že externí DLL reference ukazují na platné soubory.
3. Obnov NuGet balíčky.
4. Spusť build.
5. Otestuj COM porty, mapu a exportní dialog.

---

## Poznámky k projektu

- Projekt působí jako **interní nástroj pro vývoj / testování / servisní práci**, ne jako hotový veřejný produkt.
- Hodně logiky je soustředěno přímo v `MainWindow.xaml.cs`, takže pro další rozvoj by dávalo smysl časem oddělit:
  - map rendering,
  - serial communication,
  - playback engine,
  - export services,
  - zone editor.
- Díky tomu by byla jednodušší údržba, testování i další rozšiřování.

---

## Co by šlo případně zlepšit

- rozdělit `MainWindow.xaml.cs` do menších služeb / manager tříd,
- oddělit parsery a I/O od UI vrstvy,
- přidat lepší logování a structured logging,
- doplnit unit testy pro parsery a exportní výpočty,
- sjednotit naming a vyčistit experimentální soubory,
- doplnit dokumentaci formátů vstupních souborů.

---

## Shrnutí

`V2X Controller` je dost široký nástroj, který v jednom desktop projektu spojuje:

- mapovou vizualizaci,
- live V2X příjem,
- replay záznamů,
- kreslení zón,
- export do hardware,
- protobuf debugging.

Na GitHub README je to přesně ten typ projektu, kde se vyplatí ukázat, že nejde jen o „mapku“, ale o **kombinaci HMI, diagnostiky, parserů a exportní utility pro provozní V2X workflow**.
