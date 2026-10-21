# Restoria

**Convierte los escaneos planos de un archivo encuadernado en páginas A4 limpias y derechas — sin conexión, de Windows 7 a 11.**

La orientación se deduce de los taladros de encuadernación, se eliminan el polvo y la luz desigual, los sellos rojos siguen siendo rojos y los originales nunca se modifican. Un solo ejecutable, sin instalador, sin red.

**Pensado para** expedientes encuadernados o perforados, escaneados en plano a unos 300 DPI.
**No pensado para** fotos de móvil (perspectiva), encuadernación pegada (sin taladros) ni escáneres de cuna con corrección de curvatura.

![Seis páginas, un clic, una carpeta A4](images/demo.gif)

- **Funciona en:** Windows 7 SP1 hasta Windows 11 (.NET Framework 4.8, AnyCPU)
- **Interfaz:** dibujada íntegramente con GDI+ y con doble búfer, para que no se atasque en equipos antiguos; tema claro y oscuro
- **Código:** unas 26 000 líneas de C#, sin paquetes NuGet de terceros, se compila con el compilador que viene con Windows

---

## Descargas

Las versiones se publican en la [página de Releases](https://github.com/MC5lan/restoria/releases).

| Archivo | Qué es |
|---|---|
| `Restoria-core.zip` | **232 KB. Sin IA, sin modelos.** Descomprime y ejecuta `Restoria.exe`. La forma más rápida de probarlo |
| `Restoria-setup.exe` | Instalador en un solo archivo. Doble clic, aceptar la licencia, listo |
| `Restoria-portable.zip` | Sin instalación. Descomprime y ejecuta `setup-boot.exe` |

Los dos paquetes de mayor tamaño incluyen los recursos de IA opcionales y una capa de compatibilidad para Win7. El instalador comprueba el entorno de ejecución .NET y verifica que los componentes de IA se carguen antes de terminar. El paquete core no incluye IA ni modelos.

> **Aún sin firmar.** Como los binarios no llevan certificado de firma de código, Windows mostrará un aviso de editor desconocido. Elige *Más información → Ejecutar de todas formas*.

**Desde el código fuente:** `build.bat` produce `dist\Restoria.exe`. Las funciones de IA necesitan además copiar la carpeta `ai\` al lado (ver [Activar las funciones de IA](#activar-las-funciones-de-ia)).

---

## Otros idiomas

[English](../README.md) · [中文](README-zh.md) · [日本語](README-ja.md) · [Deutsch](README-de.md) · [Français](README-fr.md) · [Español](README-es.md) · [Русский](README-ru.md)

---

## Qué le hace a una página

Una página real, antes y después: un folio manuscrito de la dinastía Tang, de dominio público:

![Antes y después en un manuscrito Tang real](images/real-page.png)

> *Mahāratnakūṭa Sūtra*, dinastía Tang, hacia 700. Metropolitan Museum of Art, dominio público (CC0).
> El folio se recortó de la captura del museo y se convirtió a escala de grises.
> Comando: `Restoria.exe --archive page.tif out\ 300`. La hoja A4 final está [aquí](images/real-a4.png).

![Antes y después del proceso estándar](images/pipeline.png)
![Detalle antes y después](images/detail.png)
![Corrección de una página inclinada](images/deskew.png)

> Las tres imágenes de abajo se generan a partir de una página de prueba determinista (`tools\MakeDemo.bat`); las muestras reales son confidenciales.

---

## Qué hace

### La cadena de un clic

Orientación → detección de espejo → eliminación de ruido → enderezado → niveles y tono automáticos → salida A4.

- **Estándar** — equilibrio entre velocidad y limpieza. La orientación solo confía en la evidencia física de los orificios de encuadernación.
- **Exhaustivo** — elimina solo las motas más seguras y deshace su propio trabajo si empieza a comerse caracteres. **Antes deja una mota que perder un trazo de escritura a mano.** Las páginas sin orificios recurren a la orientación por OCR. Va bien para escrituras pálidas y fondos donde el contenido importa más que la velocidad.

El archivado **solo crea archivos nuevos**; la imagen original no se modifica ni en un byte. Una ejecución interrumpida continúa donde se quedó. Cada lote genera un informe con, por página, el cambio de dimensiones, el ángulo de giro y su justificación, además de recuentos y tiempos.

### Edición de una página

| Pestaña | Función |
|---|---|
| Quitar | Pincel o selección rectangular sobre el daño; cinco modos de relleno: simple, reforzado, mixto, reconstrucción de papel, IA |
| Recortar | Recorte manual o ajuste a un tablero A4 |
| Orientación | Giro manual o enderezado en un clic (método por proyección, ±20°) |
| Ajustar | Brillo, contraste, saturación, temperatura, niveles, gamma, enfoque (todos los valores se escriben con un clic) |
| Sello | Recuperar sellos rojos desvaídos |
| Lote | Aplicar parámetros fijos a toda una carpeta |
| Escanear | Hablar directamente con un escáner WIA; elegir el formato de salida (JPG/PNG/TIF) |
| Archivos | Renombrado masivo, gestión de carpetas |
| Texto | Añadir anotaciones sobre la página |

### Reconstrucción con IA opcional (MI-GAN)

- ONNX Runtime 1.14.1 manejando el modelo MI-GAN; prolonga estructuras rectas largas como los filetes de tabla sobre una zona dañada
- **Se carga en un proceso hijo, nunca en el principal.** Si el entorno de ejecución no carga, las funciones de IA se desactivan y nada más se ve afectado
- Compatibilidad con Win7 mediante cambios en la tabla de importaciones (detalles en los comentarios de `src/Ai/AiFill.cs`)

Todo el paquete `ai/` son 30 archivos, unos 59 MB, y solo hace falta para la reconstrucción con IA y la lectura de sellos.

### Garantía de calidad

- **134 pruebas sin ventana** (`--selftest`): umbrales de los algoritmos, presupuesto de deshacer, ubicación de la recuperación, conflictos de atajos
- **Regresión sobre la línea base de la interfaz** (`--layout-check`): prueba objetiva de que una refactorización de la interfaz no cambió nada
- **Cuatro puertas** (`tools/gate.py`): autopruebas, línea base de interfaz, artefacto de IA, prueba de humo de rendimiento
- **Integración continua**: GitHub Actions compila y ejecuta las puertas en cada push
- Regresión de paralelismo para la cadena de archivado: el modo exhaustivo da un resultado idéntico byte a byte con parallel=1/2/4

---

## Limitaciones conocidas

Léelo antes de dar por hecho que la herramienta se ajusta a tu material.

- **La orientación necesita evidencia física.** La decisión sobre 180° se apoya en los orificios de encuadernación. Una página sin orificios —un cuadernillo encolado, una hoja suelta, un documento perforado por dos lados— **no se girará automáticamente**. Es deliberado: girar una página de tabla que ya estaba derecha es peor que dejar una invertida que alguien note. Usa el giro manual, o el modo exhaustivo (que recurre al OCR) en páginas que lo necesiten de verdad.
- **Diseñado para la salida de escáner plano.** Las fotos de móvil tienen distorsión de perspectiva y no se procesan.
- **Los umbrales de orientación están calibrados para unos 300 DPI.** A 600 DPI la evidencia de los orificios queda fuera de la ventana calibrada y la herramienta vuelve a «sin giro».
- **El entorno de IA está fijado a ONNX Runtime 1.14.1**, la última versión que carga en Windows 7. Antes de cambiar de modelo, comprueba la compatibilidad de los operadores con `python tools\onnx_ops.py --diff ai\migan.onnx candidate.onnx`. (LaMa se descartó porque su operador FFT se comporta mal en los núcleos de CPU de esa versión.)
- **El archivado se niega a trabajar sobre** el Escritorio, la raíz de C:, las carpetas del sistema y la carpeta de perfil del usuario.
- **Todavía no hay certificado de firma de código**, así que Windows muestra un aviso de editor en el primer arranque.

---

## Compilar desde el código

**Requisitos:** Windows con .NET Framework 4.x. Nada más: ni Visual Studio ni restauración de NuGet.

```bat
build.bat
```

El resultado es `dist\Restoria.exe`. El script de compilación:

1. Detecta `vendor\onnxruntime_c_api.h`. Si existe, genera la capa de interoperabilidad (`src\Ai\OrtApi.Generated.cs`) y compila con soporte de IA; si no existe, compila sin IA y todo lo demás funciona igual.
2. Incrusta el icono (`assets\app.ico`, trazado como geometría de trazados GDI+ por `tools\IconGen.cs` — el código fuente es la única fuente del diseño) y el manifiesto (`assets\app.manifest`, que declara compatibilidad Win7–11 y `asInvoker` para que nunca pida elevación de privilegios).
3. Escribe un `.config` que declara el entorno de ejecución .NET 4.x.
4. Genera un PDB junto al ejecutable y lo renombra para que coincida con el nombre del producto, de modo que un informe de fallo procedente de una máquina en campo se pueda resolver con símbolos que correspondan.

> **Sobre el nombre del producto:** `build.bat` compila con el nombre interno `dist\PicTool.exe` y luego lo copia con el nombre publicado `dist\Restoria.exe` (leído de `appname.txt`). Ambos archivos son idénticos byte a byte. `PicTool` es el identificador interno y el espacio de nombres del ensamblado; en todos los lugares donde un usuario ve el nombre, es Restoria.

### Activar las funciones de IA

Copia la carpeta `ai\` junto al ejecutable:

```bat
xcopy /e /y ai dist\ai
```

Contiene `onnxruntime.dll`, `migan.onnx`, los modelos de `ocr\` y lasvelopment de compatibilidad de `win7\`: 30 archivos, unos 59 MB. Si no los necesitas, borra `ai\` antes de compilar.

### Ejecutar las comprobaciones

```bat
build.bat                                  :: compilar
dist\Restoria.exe --selftest               :: 1. autopruebas de algoritmos (código 0 = correcto)
dist\Restoria.exe --layout-check                 :: 2. instantánea de interfaz (comparar con la línea base)
python tools\check_resources.py --strict     :: 3. puerta de recursos (emparejamiento de LockBits)
python tools\gate.py                       :: 4. todas las puertas, incluida la prueba de rendimiento
```

La línea base de la interfaz está en `tools\golden-layout.txt`. Tras un cambio intencionado de interfaz, regenera con `--regen-layout`.

Las puertas de artefacto de IA y de rendimiento necesitan la página de prueba fija `_probe\doc_test.png`, que `Restoria.exe --make-fixture` genera de forma determinista. Está versionada, así que un clon nuevo la trae; si desaparece, la puerta la regenera y solo falla si la propia generación falla.

La línea base de rendimiento guarda una etiqueta de máquina. Una línea base procedente de otra máquina se informa en lugar de compararse en silencio: reconstruye la local con `--regen-perf`.

---

## Diagnóstico por línea de órdenes

Modos sin ventana, para scripts e informes de errores. Los informes van a `%TEMP%\PicTool-*.txt`, los códigos de salida son limpios y no aparece ningún diálogo.

| Opción | Para qué sirve |
|---|---|
| `--selftest` | 134 autopruebas de algoritmos e interacción; código 0 = todas correctas |
| `--layout-check` | Instantánea de interfaz (debe ser idéntica línea a línea antes y después de una refactorización) |
| `--archive <entrada> <salida> [ppp] [opciones]` | Ejecuta la cadena sin ventana |
| `--skew-test <archivo o carpeta> [n]` | Detección de enderezado con comparación de control sobre el borde negro |
| `--ai-check` / `--ai-check-silent` | Comprobación del entorno de IA y los modelos (silent para instalación desatendida) |
| `--ai-fill <entrada> <salida> [x y w h]` | Reconstruye una zona con IA y la guarda en disco, para comparar sin conexión |
| `--paper-fill <entrada> <salida> [x y w h]` | Reconstruye una zona de papel y la guarda en disco |
| `--make-fixture [ruta]` | Exporta la página de prueba determinista de las puertas |
| `--bench [golden] [--regen]` | Tiempo por operador e huellas del resultado |
| `--wheeltest[=carpeta]` | Enrutado de la rueda: lienzo, barra izquierda y barra derecha |
| `--uityping` | Comprobación de la regla de foco, predicado antiguo y nuevo en paralelo |
| `--undotest` | Presupuesto de deshacer y reversión a nivel de fotograma |
| `--cursortest` | Comprobación de la forma del cursor propio, deja una captura |
| `--droptest` | Comprobación del árbol de recepción de arrastrar y soltar |

Interruptores de entorno: `PICTOOL_AI_TRACE=1`, `PICTOOL_OCR_TRACE=1`, `PICTOOL_SKEW_NOGUARD=1` y `PICTOOL_RECOVERY_ROOT` (redirige la zona de recuperación; lo usan los modos de prueba para que nunca toquen datos reales del usuario).

---

## Estructura

```
├── src/                  Todo el código fuente (68 archivos .cs, unas 26 000 líneas)
│   ├── Program.cs        Punto de entrada, captura global de excepciones, reparto de diagnósticos
│   ├── Core/             Suite de autopruebas, banco de pruebas, versión, recuperación, puente de mapas de bits
│   ├── Platform/         Sonda del sistema, escaneo WIA, utilidades de archivos
│   ├── Imaging/          Algoritmos de imagen puros (sin dependencia de la interfaz): limpieza, mejora, reparación, archivado
│   ├── Ai/               Capa opcional de IA: interoperabilidad con ONNX, MI-GAN, juez de orientación por OCR
│   └── Ui/               Interfaz e interacción (el único lugar que referencia WinForms)
├── assets/               Iconos y manifiestos. El icono lo traza tools\IconGen.cs
│                         como geometría de trazados GDI+; el código es la única fuente del diseño
├── _probe/               Página de prueba fija para las puertas (se regenera con --make-fixture)
├── tools/                Cadena de herramientas de compilación y verificación
│   ├── gate.py           Las cuatro puertas: autopruebas + interfaz + artefacto de IA + rendimiento
│   ├── check_resources.py Puerta de recursos (emparejamiento de LockBits)
│   ├── onnx_ops.py       Análisis de compatibilidad de operadores ONNX (ejecútalo antes de cambiar de modelo)
│   ├── archive_demo.py   Generador de imágenes de comparación antes/después
│   ├── make_package.ps1  Cadena de empaquetado (compilar → ensamblar → firmar → autoverificar)
│   └── install/          Código del instalador y del desinstalador
├── ai/                   Paquete opcional de IA: entorno ONNX + MI-GAN + modelos OCR + capa de compatibilidad de Win7
├── vendor/               onnxruntime_c_api.h (genera la capa de interoperabilidad)
├── docs/                 Traducciones y notas de arquitectura
├── build.bat             Compilación con un solo comando
├── CHANGELOG.md          Historial de versiones
└── THIRD_PARTY_NOTICES   Licencias de todos los componentes de terceros incluidos
```

Las dependencias fluyen en una sola dirección; no se permiten referencias inversas:

```
Ui ──→ Imaging / Ai / Platform / Core
Ai ──→ Platform (SysInfo)
Imaging ──→ Platform (algunos)
Platform ──→ (nada)
```

`Imaging` y `Ai` no deben mencionar `System.Windows.Forms`; los algoritmos reciben parámetros y devuelven mapas de bits nuevos en lugar de modificar un estado global.

---

## Componentes de terceros y licencias

El propio código de Restoria se distribuye bajo licencia MIT (véase [LICENSE](../LICENSE)). Los componentes siguientes viajan en la versión publicada y permanecen bajo sus propias licencias:

| Componente | Versión | Copyright | Licencia |
|---|---|---|---|
| ONNX Runtime (`ai\onnxruntime.dll`, `ai\win7\onnxruntime.dll`) | 1.14.1 | © Microsoft Corporation | MIT |
| Modelos de detección y reconocimiento PP-OCRv3 y diccionario (`ai\ocr\`) | PP-OCRv3 | © PaddlePaddle Authors | Apache-2.0 |
| Modelo de reconstrucción MI-GAN (`ai\migan.onnx`) | 512, ICCV 2023 | © 2024 Picsart AI Research | MIT |
| Shim de compatibilidad del conjunto de API de Win7 (`ai\win7\*.dll`) | — | Reenviadores de Microsoft | MIT |

Los textos completos de las licencias están en **[THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES)**.

Estos componentes sirven **solo** para la reconstrucción con IA y la lectura de sellos. La cadena principal —orientación, enderezado, eliminación de ruido y manchas, niveles, tono y salida A4— es C# puro sin dependencia nativa y funciona por completo sin la carpeta `ai\`. El programa sondea, en un proceso hijo, si el entorno de IA puede cargarse, y desactiva esas funciones con elegancia si no; ninguna ruta de código de IA se ejecuta jamás en el proceso principal.

## Agradecimientos

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) — motor de inferencia multiplataforma
- [MI-GAN](https://github.com/Picsart-AI-Research/MI-GAN) — modelo de reconstrucción de imágenes, ICCV 2023
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — modelos de detección y reconocimiento PP-OCRv3