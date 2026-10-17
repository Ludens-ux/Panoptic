# Isla dinámica — especificación para implementar

Rediseño del widget de `Panoptic` (antes "Monitor del sistema") (C#, WinForms, .NET Framework 4.8) como una **isla dinámica** al estilo Windows 11. La referencia visual es `Isla Monitor.dc.html` (opciones 1a, 1b y 1c).

> **Para Claude Code:** implementá esto dentro del proyecto existente. Respetá la arquitectura actual: `Surface` (pintado en modo inmediato con `Hit`), `Theme`, `ResColors`, `Native.SetBackdrop`, `Settings` y el registro `Software\Panoptic`. No agregues dependencias (nada de WPF/WinUI/NuGet): todo tiene que seguir compilando con `build.ps1`.

---

## 1. Qué hay que construir

Una nueva ventana `IslandForm : Surface` (archivo `src/IslandForm.cs`) que reemplaza al widget como modo de visualización. **El `WidgetForm` actual se mantiene**: en Configuración se elige el estilo entre **Widget** (el actual) e **Isla**.

La isla tiene dos estados:

| Estado | Qué muestra | Tamaño (DIP, a 100 %) |
|---|---|---|
| **Compacta** | Una barra con mini anillos de CPU, memoria y temperatura, más ↓ red | 300 × 38 |
| **Expandida** | Los 6 anillos, la red con gráfico y las acciones | 560 × 244 |

En todos los estados las esquinas son de **8 px, las nativas de Windows 11** (las mismas que usan los menús, el panel de Configuración rápida y los paneles flotantes). Los controles internos, como botones y celdas, van con 4 px (`ControlCornerRadius`).

También tiene **estados contextuales** temporales (sección 6), que reemplazan el contenido compacto durante unos segundos.

Todas las medidas se escalan con el DPI usando el mismo `scale` que ya usa `Surface`.

---

## 2. Ventana y materiales

- `FormBorderStyle.None`, `ShowInTaskbar = false`, `TopMost = true`. Estilos extendidos `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, para que no robe el foco ni aparezca en Alt+Tab.
- **Fondo:** acrílico (`Native.SetBackdrop(Handle, DWMSBT_TRANSIENTWINDOW)`) cuando `th.Glass` es verdadero. Si no hay transparencia, usar un sólido `#202020` en oscuro y `#F3F3F3` en claro.
- **Tinte sobre el acrílico:** `rgba(32,32,32,.78)` en oscuro y `rgba(243,243,243,.80)` en claro.
- **Borde:** 1 px con `rgba(255,255,255,.09)` en oscuro y `rgba(0,0,0,.08)` en claro. Usar `DWMWA_BORDER_COLOR` como ya hace `WidgetForm`.
- **Esquinas:** `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND` (8 px), como ya hace `WidgetForm`. **No usar `SetWindowRgn` ni ventanas en capas (`WS_EX_LAYERED`)**: el recorte con región deja bordes dentados y hace perder el borde y la sombra de DWM, y las ventanas en capas pierden el acrílico. Con el redondeo de DWM, el borde queda suavizado y **el acrílico, el borde de 1 px y la sombra los dibuja el sistema y se ajustan solos** en cada cambio de tamaño.
- **Nunca reemplazar el acrílico por un fondo opaco**, salvo cuando el usuario desactive los Efectos de transparencia: ahí se usa el sólido del tema, igual que el resto de la app.
- **Pintado sobre el acrílico:** usar el mismo método de `Surface`/`Vibrancy` que ya funciona en el widget (sin `TransparencyKey`) y dibujar solo el contenido. No pintar un rectángulo de fondo: el tinte lo aporta el material.
- **Sombra:** la que da DWM. No dibujar sombras propias.

### Posición

- **Arriba al centro** (opción 1a, la predeterminada): `x = área de trabajo.centro − ancho/2`, `y = área de trabajo.top + 10`.
- **Sobre la barra de tareas** (opción 1b): `y = área de trabajo.bottom − alto − 10`. En este modo la isla crece **hacia arriba**: se mantiene fijo el borde inferior.
- La isla siempre queda centrada horizontalmente: al animar el ancho, también se mueve `x`.
- Se usa el monitor principal o el que se elija en Configuración. Hay que reposicionarla con `SystemEvents.DisplaySettingsChanged` y cuando cambia el área de trabajo (`WM_SETTINGCHANGE` con `SPI_SETWORKAREA`).
- Ocultarla cuando haya una app en pantalla completa (`SHQueryUserNotificationState` devuelve `QUNS_BUSY` o `QUNS_RUNNING_D3D_FULL_SCREEN`).

---

## 3. Animación (el "resorte")

- La transición entre compacta y expandida dura **500 ms** con la curva `cubic-bezier(.3, 1.3, .5, 1)`, que da un rebote leve. Se animan ancho y alto con `SetWindowPos(... SWP_NOACTIVATE | SWP_NOZORDER)` en cada cuadro; el radio queda fijo en 8 px y DWM lo mantiene.
- Para que no parpadee al agrandar: el contenido se pinta en `WM_PAINT` con doble búfer y se responde a `WM_ERASEBKGND` sin hacer nada. Si DWM mostrara un borde negro un instante, agrandar primero la ventana al tamaño final, pintar y recién después animar el contenido (técnica de "lienzo máximo").
- El contenido no se anima por separado: al empezar a expandir se pinta el contenido nuevo con un fundido de opacidad de 0 a 1 entre los 120 y los 320 ms. Al colapsar se hace lo inverso, y más rápido (200 ms).
- Los anillos animan su valor con suavizado (como ya hace el widget), unos 600 ms con *ease out*.
- Usar un `Timer` de 16 ms solo mientras hay una animación activa; si no, apagarlo.
- Si en Windows están desactivados los **Efectos de animación** (`SPI_GETCLIENTAREAANIMATION`), el cambio de estado es instantáneo.

Implementación sugerida de la curva (Bézier cúbica resuelta por bisección sobre x):

```csharp
static double CubicBezier(double t, double x1, double y1, double x2, double y2)
{
    double lo = 0, hi = 1, u = t;
    for (int i = 0; i < 20; i++) {
        u = (lo + hi) / 2;
        double x = 3*(1-u)*(1-u)*u*x1 + 3*(1-u)*u*u*x2 + u*u*u;
        if (x < t) lo = u; else hi = u;
    }
    return 3*(1-u)*(1-u)*u*y1 + 3*(1-u)*u*u*y2 + u*u*u;   // puede superar 1 (rebote)
}
```

---

## 4. Contenido

**Colores:** usar `ResColors.*(th)`, que ya existen. Los colores de la maqueta son solo de referencia:

| Métrica | Fuente | Color de la maqueta (oscuro) |
|---|---|---|
| CPU | `ResColors.Cpu` | `#F4A6C8` (en la app es el acento del sistema) |
| Memoria | `ResColors.Memory` | `#C4A0F5` |
| Disco | `ResColors.Disk` | `#6CCB5F` |
| GPU | `ResColors.Gpu` | `#60CDFF` |
| Temperatura | `ResColors.Temp` | `#FF8A80` |
| Batería | `ResColors.Battery` | `#A8E6A0` |
| Red | `ResColors.Net` | `#FFB38A` |

- La **pista** de cada anillo es su color al 22 % de opacidad sobre el fondo.
- **Tipografía:** `Segoe UI Variable Text`. Los números grandes van en `Segoe UI Variable Display Semibold`, con cifras tabulares (en GDI+ alcanza con medir con un ancho fijo, `"88%"`, para que no salte el texto).
- **Texto secundario:** blanco al 55–62 % en oscuro y negro al 60 % en claro. Usar `GlassText` como el resto de la app.

### 4.1 Compacta (alto 38)

De izquierda a derecha, con padding `0 16 0 12` y separación de 14:

1. **Ícono de la app:** un cuadrado de 22 × 22, radio 4, con fondo del color de memoria al 18 % y el glifo de pulso en ese color.
2. **Tres grupos `[anillo 16 px] [valor]`:** CPU, memoria y temperatura. El anillo tiene un trazo de 2,8 px, extremo redondeado y empieza a las 12 en punto, en sentido horario. El valor va a 13 px semibold ("6%", "95%", "61°").
3. Un separador vertical de 1 × 16 px a 12 % de opacidad.
4. `↓ 42 Kbps` a 12 px, en texto secundario.

### 4.2 Expandida (560 × 244, padding 14/16/16, separación vertical 14)

**Encabezado (alto 28):**
- A la izquierda: el ícono (24 × 24), "Monitor" a 14 px semibold y "Actualizado hace 1 s" a 12 px en texto secundario.
- A la derecha, dos botones de ícono de 32 × 28 con radio 4: **Fijar** (glifo Pin `E718`) y **Abrir ventana principal** (`E8A7`). Al pasar el mouse, fondo `HoverOverlay`.

**Grilla de anillos:** 6 columnas iguales con separación de 4.
- Cada celda tiene el anillo de 64 px (trazo de 3,5 px) con el valor centrado (16 px, Display Semibold), debajo la etiqueta (12 px) y debajo el subtítulo en el color de la métrica (11 px).
- Los subtítulos son los mismos que en el widget actual: GHz, GB usados, KB/s, MB de GPU, "Normal"/"Alta" para la temperatura y "Cargando"/tiempo restante para la batería.
- Al pasar el mouse, la celda toma fondo `HoverOverlay` con radio 4.
- **Clic en una celda:** abre la ventana principal en Rendimiento > esa métrica.
- Si no hay batería, la celda de batería se reemplaza por **Red** (anillo con % del ancho de banda del adaptador).
- Si no hay GPU o temperatura, esa celda se quita y la grilla pasa a 5 o 4 columnas: el ancho total se recalcula a `columnas × 90 + 32`.

**Fila de red:** radio 4, fondo `rgba(255,255,255,.04)` y borde a 5 % de opacidad, padding `8 12`.
- Contiene "Wi-Fi" (o el nombre del adaptador) en el color de red, el gráfico de los últimos 40 s de bajada (polilínea de 1,5 px, que ocupa todo el ancho disponible), `↓ 42 Kbps` y `↑ 125 Kbps`.

### 4.3 Variante "sobre la barra de tareas" (opción 1b)

Si la posición es la de la barra de tareas, la expandida usa **barras en lugar de anillos** y suma procesos:

- **Tamaño:** compacta 260 × 36 y expandida 402 × 434 (esquinas de 8 px de DWM).
- **Compacta:** `CPU [gráfico de 64×18 de los últimos 24 s] 6 %` y, alineado a la derecha, `Mem 95 %` con el valor en el color de memoria.
- **Expandida:** el contenido queda pegado abajo y la isla compacta sigue visible como última fila.
  - Seis filas en una grilla `84 | 1fr | 44`: etiqueta con su subtítulo, una barra de 4 px con radio 2 (pista al 22 %) y el valor alineado a la derecha.
  - Debajo, separada por una línea de 1 px a 7 %, la lista **"Más consumo"** con los 3 procesos de más CPU (ícono 16 px, nombre, CPU y memoria), con filas de 32 px.
  - **Clic en un proceso:** abre Procesos con ese proceso seleccionado.
  - **Clic derecho en un proceso:** "Finalizar tarea".

---

## 5. Interacción

| Acción | Resultado |
|---|---|
| Clic en la isla | Alterna compacta ↔ expandida |
| Mouse encima (si "Expandir al pasar el mouse" está activo) | Expande después de 250 ms; colapsa 400 ms después de que el mouse sale |
| Clic fuera / Esc / pérdida de foco de la app | Colapsa |
| Clic derecho | `FluentMenu` con: Posición (Arriba / Sobre la barra de tareas), Expandir al pasar el mouse, Mostrar alertas en la isla, Abrir Monitor, Ocultar isla |
| Rueda del mouse sobre la compacta | Cambia qué métricas muestra la compacta (CPU·Mem·Temp → CPU·GPU·Disco → Red) |
| `Ctrl + Mayús + M` | Sigue abriendo la ventana principal (no cambia) |

- **Teclado y accesibilidad:** cada `Hit` tiene que tener nombre accesible (por ejemplo, "CPU 6 %, 3,09 GHz"), como ya hace `SurfaceAccessible`. Con Tab se recorren los botones y las celdas; con Enter o Espacio se activan.

---

## 6. Estados contextuales (opción 1c)

Son eventos que **toman la isla compacta** durante 4–6 s (o hasta que el usuario interactúe) y después vuelven a la vista normal. La isla agranda o achica su ancho con la misma animación de resorte para ajustarse al contenido.

| Estado | Disparador (ya existe en `Activity` / alertas) | Contenido | Alto | Duración |
|---|---|---|---|---|
| Reposo | Todo por debajo del 10 % durante más de 60 s (opcional) | Punto verde de 6 px y "6 %" | 32 | permanente |
| Descarga activa | Bajada > 5 MB/s sostenida 5 s | "Wi-Fi", gráfico de 90 × 20 y `↓ 12,4 MB/s` | 38 | mientras dure |
| Alerta de temperatura | Se supera el umbral de Configuración | "92°" en Critical a 18 px, "Temperatura alta / Supera el umbral de 85°" y botón **Ver** | 44 | hasta tocar o 10 s |
| Proceso acaparando CPU | Un proceso usa más del 40 % durante 2 min | Ícono del proceso, "msedge.exe / 48 % CPU durante 2 min" y botón **Finalizar tarea** con fondo de acento | 44 | hasta tocar o 10 s |
| Cargador conectado | Evento de energía (enchufe) | Batería dibujada, "72 %" y "Cargando · lleno en 48 min" en color de batería | 38 | 4 s |
| Dispositivo USB | Evento de dispositivo conectado | Ícono, "Kingston DataTraveler" y "conectado · E: 28,6 GB libres" | 38 | 4 s |

- **Alertas críticas:** se mantiene el acrílico; se pinta encima un tinte `Critical` al 14 % y se cambia el borde con `DWMWA_BORDER_COLOR` a `Critical`. Al terminar la alerta, volver al borde normal.
- **Botones dentro de la isla:** alto 32, radio 4 (estilo de botón de Windows 11) y padding horizontal 14. El secundario usa `ControlFill`; el primario, fondo `th.Accent` con texto `th.OnAccent`.
- **Cola:** si llegan varios eventos, se muestran de a uno y el crítico va primero. No repetir el mismo evento dentro de los 5 min.
- Estos estados **no reemplazan** la notificación de Windows que ya se envía; la complementan. Se agrega la opción "Mostrar alertas en la isla".

---

## 7. Configuración (`Settings` + página Configuración)

Agregar estas claves en `Software\Panoptic`:

| Clave | Tipo | Predeterminado |
|---|---|---|
| `WidgetStyle` | `"Widget"` \| `"Isla"` | `"Isla"` |
| `IslandPosition` | `"Top"` \| `"Taskbar"` | `"Top"` |
| `IslandHoverExpand` | bool | `false` |
| `IslandAlerts` | bool | `true` |
| `IslandColorMode` | `"PerMetric"` \| `"Accent"` | `"PerMetric"` (con `Accent`, todos los anillos usan `th.Accent`) |
| `IslandCompactSet` | int (0–2) | `0` |

En la página Configuración, dentro de la sección del widget, agregar un `seg` Widget / Isla y, si se elige Isla, los controles de arriba.

---

## 8. Tema claro

Mismo diseño con los valores de `Theme` en claro: el tinte, el borde, los textos y los colores de métrica cambian solos con `ResColors(th)`. Hay que verificar el contraste del texto secundario sobre acrílico claro, igual que hoy con `GlassText`.

---

## 9. Vista previa / pruebas

- Extender `Preview` (`Panoptic.exe --render carpeta [dark|light]`) para que genere `isla-compacta.png`, `isla-expandida.png`, `isla-barra.png` y una captura por cada estado contextual, con los datos de ejemplo de `DemoHost`.
- Comparar visualmente con `Isla Monitor.dc.html`.

## 10. Criterios de aceptación

- [ ] Compila con `build.ps1` sin dependencias nuevas.
- [ ] La isla no aparece en la barra de tareas ni en Alt+Tab, y no roba el foco.
- [ ] La animación compacta ↔ expandida tiene un rebote visible y no parpadea (usar doble búfer, como en `Surface`).
- [ ] Esquinas de 8 px de DWM, con bordes suavizados, y el acrílico visible en todos los estados y tamaños (nunca un fondo opaco con la transparencia activada).
- [ ] El uso de CPU en reposo es menor al 0,5 %: el timer de animación se apaga cuando no hay animación.
- [ ] Se respetan la transparencia y la animación del sistema, y los temas claro y oscuro, en vivo.
- [ ] Funciona en 100 %, 125 %, 150 % y 200 % de escala, y con varios monitores.
- [ ] Se oculta con apps en pantalla completa.
- [ ] Los estados contextuales se encolan y no se repiten.
- [ ] Se puede elegir entre el widget anterior y la isla sin reiniciar.
