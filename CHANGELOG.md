## [1.5.0] - 2026-11-10

- MINOR DH-20261110-CRM-AGENDA-UNIFIED-077: autorización CRM de agendas por scopes de lectura global, creación de actividades en agenda de otros vendedores, participantes/contactos, minuta y compromisos vinculados.
- CI, pruebas y staging pendientes; producción no autorizada. REST y realtime existentes conservados.

## [1.4.6] - 2026-11-10

- PATCH DH-20261110-CRM-HISTORICAL-ACTIVITIES-075: permite registrar actividades CRM con fechas anteriores, conservando fechas UTC y marcando como completadas las que ya finalizaron. Las actividades completadas no generan recordatorios retroactivos. Mantiene reglas de fechas futuras de compromisos y seguimientos, permisos, minutas y las APIs existentes.
- Coordinado con DholeWeb/DholeSystem 1.17.18; sin migraciones de datos ni cambios de SignalR. CI y validación en staging pendientes.

## [1.4.5] - 2026-11-10

- DH-20261110-CRM-PROSPECT-SELLER-072: endpoint GET /leads/assigned-executives, resuelve nombres de vendedores de prospectos mediante Auth y verifica permisos de lectura por cada registro; respeta read-all y no expone prospectos ajenos.
- Sin migración de base de datos; creación y reasignación ya admiten executiveUserId y crm.lead.assign. Ecosistema Dhole 1.17.16.

## [1.4.4] - 2026-11-10

- DH-20261110-CRM-PROSPECT-ROUTES-QUOTES-069: normalización UPPER CASE de nombres de prospectos en creación y edición, endpoints autorizados de oportunidades y cotizaciones vinculadas al prospecto.
- Release global Dhole 1.17.15, sin migraciones.

## [1.4.3] - 2026-11-10

- PATCH DH-20261110-CRM-ACTIVITY-FULL-DETAIL-068: añade endpoints autorizados de solo lectura para consultar acuerdos generados por actividades y reconstruir su actividad principal desde el historial CRM. Traslada la implementación probada en develop, conservando los arreglos recientes de identidad comercial.
- Release global Dhole 1.17.14, sin migraciones ni modificaciones a NotificationsHub/SignalR.

## [1.4.2] - 2026-11-10

- PATCH DH-20261110-PRICING-CRM-REQUEST-IDENTITY-067: el selector autorizado de cuentas y prospectos permite resolver selectedClientId desde solicitudes existentes, sin perder filtros de permisos por cuenta, prospecto o vendedor y sin paginar fuera de su alcance. Release global Dhole 1.17.12, CI y staging pendientes.

## [1.4.1] - 2026-11-10
- PATCH DH-20261110-CRM-ACCOUNT-SEARCH-064: GET /api/crm/accounts busca por nombre comercial, razón social, cédula o identificación fiscal parcial y completa, con y sin guiones, puntos o espacios.
- Se respeta la autorización original, los filtros y la paginación SQL. Incluye registros históricos sin identificación normalizada, sin migraciones ni cambios a SignalR/REST. Ecosistema 1.17.9, candidato pendiente de CI/staging; sin autorización de producción.

## [1.4.0] - 2026-11-09
- Búsqueda autorizada de cuentas y prospectos desde solicitudes de Pricing y asignación comercial según CRM.

# Changelog

## [1.3.1] - 2026-11-08

- Corrige la falla de entrega de todos los recordatorios CRM: registra IConnectionMultiplexer antes de resolver RedisStreamPublisher en los workers.
- Evita falsos estados saludables: el worker exige un publicador Redis válido al iniciar, y continúa utilizando el outbox idempotente persistente para recuperación.
- Añade prueba con Redis real que resuelve la dependencia y publica un evento de notificación de prueba sin destinatario externo.

## [1.3.0] - 2026-11-07

- Programa avisos de CRM por sistema y correo antes y al inicio exacto de la actividad, con tolerancia a retrasos del planificador.
- Configura repetición de actividades y recordatorios periódicos hasta su resolución.
- Preserva entrega idempotente Redis/SignalR y reencola avisos tras modificar una actividad.
- Incluye migración y prueba PostgreSQL de recurrencia.

## [1.2.1] - 2026-11-05

- Alinea Dhole CRM al release global 1.3.1 y conserva flujos de staging y producción separados.
- Actualiza SemVer nativo y validación de versiones sin modificar la rama main.

## [1.2.0] - 2026-11-05

- Incorporates universal Dhole SemVer policy with GitHub Actions checks.
- Synchronizes service assembly metadata with the ecosystem version.
- Adds permanent Codex instructions and coordinated release requirements.
