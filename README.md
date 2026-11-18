# ARAR-C3D

Complemento de ingeniería civil para **Autodesk Civil 3D 2026 (64 bits)** desarrollado en **C# (.NET 8 / WPF)**. Agrupa herramientas de señalización vial, análisis de corredores, tablas de volúmenes/alineaciones y utilidades de dibujo bajo la pestaña propia **ARAR-C3D** en la cinta de opciones.

---

## Herramientas y comandos incluidos

El complemento registra automáticamente todas las herramientas mediante `ToolCatalog` y las expone tanto en la cinta (**ARAR-C3D**) como desde la línea de comandos de Civil 3D:

| Herramienta | Comando | Descripción |
| :--- | :--- | :--- |
| **Acerca de ARAR-C3D** | `ARAR_ACERCA` | Ventana informativa del complemento. |
| **Información del dibujo** | `ARAR_INFO_DIBUJO` | Muestra información general del DWG activo. |
| **Dibujar Marca vial** | `ARAR_MARCA_VIAL` | Dibuja marcas viales 2D (polilínea continua o discontinua con ancho global configurable). |
| **Marca vial 3D** | `ARAR_MARCA_VIAL_3D` | Genera sólidos 3D a partir de marcas viales existentes. |
| **Subir sólidos a superficie** | `ARAR_SUBIR_SOLIDOS_SUPERFICIE` | Proyecta sólidos 3D sobre una superficie de Civil 3D tomando como referencia su base. |
| **Cebreado** | `ARAR_CEBREADO` | Genera cebreados nativos con polilíneas y sombreado entre borde de carril y berma, con selección de sentido (*Entrada* / *Salida*). |
| **Visor de secciones** | `MIE_SECCIONES` | Visor interactivo de secciones transversales de corredor con terreno TIN, perfil longitudinal, sombreado desmonte/terraplén, favoritos en el DWG y dibujo/exportación PNG. *(Comandos auxiliares: `MIE_SECCIONES_CURSOR`, `MIE_SECCION_DIBUJAR`, `MIE_SECCIONES_CENTRAR`)*. |
| **Tabla de volúmenes** | `ARARTABLAVOLUMENES` | Genera tablas de volúmenes en el dibujo *(actualización con `ARARACTUALIZARTABLAVOLUMENES`)*. |
| **Tabla de puntos de alineación** | `ARARTABLAPUNTOS` | Genera tablas con puntos clave de alineaciones. |
| **Exportar tablas a Excel** | `ARAREXPORTAREXCEL` | Exporta tablas generadas por ARAR-C3D a libros `.xlsx` (mediante `ClosedXML`). |

---

## Estructura del proyecto

```text
C3D.ARAR/
├── C3D.ARAR.csproj       # Proyecto principal (.NET 8 Windows + WPF, x64)
├── Core/                 # Inicialización del add-in, cinta (Ribbon) y catálogo de herramientas
├── Models/               # Modelos de datos y opciones de usuario
├── Services/             # Lógica geométrica, muestreo de terreno, cálculo de secciones, Excel y logging
├── Tools/                # Comandos [CommandMethod] y definición de botones ITool
├── UI/                   # Ventanas WPF y controles gráficos (Canvas)
├── Resources/            # Iconos PNG embebidos (incluye Resources/Icons/)
├── Installer/            # Bootstrapper e instrucciones del paquete .bundle para Civil 3D
└── Tests/                # Pruebas unitarias (xUnit) y runner de regresión
```

---

## Requisitos y compilación

- **Autodesk Civil 3D 2026** (64 bits) instalado.
- **.NET 8 SDK** y **Visual Studio 2022** (o superior) con carga de trabajo de escritorio .NET.
- Dependencia NuGet: `ClosedXML` (`0.105.1`).
- Las referencias de AutoCAD/Civil 3D (`AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll`, `AdWindows.dll`, `AecBaseMgd.dll`, `AeccDbMgd.dll`) se resuelven desde `Civil3DInstallDir` (por defecto `C:/Program Files/Autodesk/AutoCAD 2026`) con `Private="false"` (`Copy Local = False`).

### Pasos para compilar

1. Abre `C3D.ARAR.csproj` en Visual Studio 2022.
2. Selecciona configuración **Release** y plataforma **x64**.
3. Verifica que `Civil3DInstallDir` en `C3D.ARAR.csproj` coincida con tu ruta de instalación de Civil 3D 2026.
4. Compila la solución (`dotnet build -c Release`).

---

## Instalación y uso en Civil 3D

### Opción 1: Instalador automático (`.bundle`)
El instalador (`Installer/Bootstrapper.cs`) copia `PackageContents.xml`, `ARAR.Civil3D.dll` y `ARAR.Civil3D.deps.json` en:

```text
%APPDATA%\Autodesk\ApplicationPlugins\ARAR-C3D.bundle
```

Al iniciar Civil 3D 2026, el complemento se carga automáticamente y aparece la pestaña **ARAR-C3D**.

### Opción 2: Carga manual para desarrollo (`NETLOAD`)
1. Abre Civil 3D 2026.
2. Ejecuta el comando `NETLOAD`.
3. Selecciona `bin\Release\net8.0-windows\ARAR.Civil3D.dll` (o tu carpeta de compilación).
4. Usa los botones de la pestaña **ARAR-C3D** o escribe el comando correspondiente en la barra de comandos.

---

## Pruebas

El proyecto incluye pruebas unitarias con **xUnit** en `Tests/ARAR.Civil3D.Tests.csproj` (que no requieren Civil 3D abierto para validar lógica pura como `DashPattern` y `VolumeTableTextFormat`):

```bash
dotnet test Tests/ARAR.Civil3D.Tests.csproj
```
