# Política de privacidad de Panoptic

*Última actualización: 17 de octubre de 2026*

Panoptic es un monitor del sistema para Windows desarrollado por @villargonza. Esta política explica qué datos usa el programa, dónde quedan y quién puede verlos.

## Resumen

- **Panoptic no envía tus datos a ningún servidor.** No tiene cuentas, publicidad, estadísticas de uso ni telemetría.
- **Todo lo que registra queda en tu PC**, en archivos que podés ver y borrar cuando quieras.
- El autor **no recibe ni puede ver** ninguna información de tu equipo.

## Qué datos lee el programa

Para mostrar el estado de la PC, Panoptic lee información del propio Windows:

- **Rendimiento:** uso de CPU, memoria, disco, red, GPU, temperatura y batería.
- **Programas en ejecución:** nombre, ruta, uso de recursos y conexiones de red de cada proceso.
- **Red:** adaptadores, dirección IP local y nombre de la red Wi-Fi.
- **Eventos del sistema:** dispositivos USB conectados, suspensión, bloqueo de sesión, errores del Visor de eventos, programas de inicio y servicios.

Si activás las **funciones de administrador**, además lee:

- La **línea de comandos** de cada programa que se ejecuta y qué programa lo lanzó.
- El **registro de seguridad de Windows**: inicios de sesión (con el nombre de usuario y el equipo de origen), cuentas y grupos modificados, y tareas programadas creadas.
- El **tráfico de red** y los **archivos** que lee y escribe cada programa (los archivos, solo mientras la página Archivos está abierta).
- La **salud SMART** de los discos.

Las líneas de comandos pueden incluir datos que otros programas reciben al iniciarse (por ejemplo, rutas de archivos o parámetros). Panoptic los guarda tal cual, solo en tu PC.

## Dónde se guardan

Todo queda en tu usuario de Windows:

| Qué | Dónde |
|---|---|
| Registro de actividad | `%LOCALAPPDATA%\Panoptic\registro.log` |
| Historial de rendimiento (promedios por minuto) | `%LOCALAPPDATA%\Panoptic\historial.csv` |
| Listas de programas de inicio y servicios (para detectar cambios) | `%LOCALAPPDATA%\Panoptic\` |
| Preferencias | Registro de Windows, `HKEY_CURRENT_USER\Software\Panoptic` |

- El historial de rendimiento se conserva la cantidad de días elegida en Configuración (hasta 30).
- El registro de actividad se conserva hasta que lo borres.
- Los informes que exportás (HTML o CSV) se guardan solo donde vos elijas.

## Conexiones a internet

Panoptic no se comunica con servidores propios ni de terceros. Hay dos casos en los que se usa la red:

- **Nombres de los servidores remotos:** en la página Conexiones, para mostrar el nombre de cada dirección IP, Windows hace una consulta de DNS inversa al servidor de DNS que tengas configurado (normalmente el de tu proveedor de internet o tu router). Es la misma consulta que hace cualquier herramienta de red.
- **"Ver información de la IP en la web":** solo si elegís esa opción del menú, se abre tu navegador en ipinfo.io con la dirección elegida. A partir de ahí rige la política de privacidad de ese sitio.

## Integración con WriteBlocker

Si tenés instalado WriteBlocker y la integración activada, Panoptic lee su registro local (`%LOCALAPPDATA%\WriteBlocker\actividad.log`) para mostrar esos eventos en la misma línea de tiempo. No lo modifica ni lo envía a ningún lado.

## Cómo borrar tus datos

- **Desde el programa:** Configuración → Datos → Carpeta de datos → **Abrir**, y borrá los archivos.
- **Por completo:** cerrá Panoptic (clic derecho en el ícono → Salir), desactivá antes las funciones de administrador en Configuración, y borrá la carpeta `%LOCALAPPDATA%\Panoptic` y la clave `HKEY_CURRENT_USER\Software\Panoptic`.

## Menores de edad

Panoptic no está dirigido a menores y no recopila datos personales de nadie: todo queda en el equipo donde se usa.

## Cambios en esta política

Si el funcionamiento del programa cambia, esta política se actualiza en este mismo archivo, con la fecha de la última modificación.

## Contacto

Para consultas sobre esta política, abrí un *issue* en el repositorio: https://github.com/Ludens-ux/Panoptic/issues
