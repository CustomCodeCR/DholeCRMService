## [1.23.0] - 2026-11-11

- MINOR DH-20261111-CRM-ACCOUNT-INLINE-RELATIONS-107: permite registrar contactos y direcciones opcionales al crear una cuenta en una sola transacción EF Core. Incluye permisos, validación de país, contactos principales y jerarquías geográficas, sin alterar las APIs REST existentes.
- La creación de cuentas sin contactos ni direcciones conserva su funcionamiento previo. CRM 1.22.0 → 1.23.0; versión global del ecosistema Dhole 1.38.0. CI y staging pendientes; no se autoriza despliegue a producción.

## [1.22.0] - 2026-11-11

- MINOR DH-20261111-CRM-QUOTE-MULTI-CONTACTS-102: configuración por cuenta de todos los destinatarios de cotizaciones futuras. Elimina el selector de QUO y permite elegir varios contactos con DhMultiSelect; se preservan las opciones solo vendedor o cliente con CC vendedor asignado.
- CRM guarda y valida la lista y migra el contacto único anterior; Pricing comprueba todos los contactos antes de preparar el PDF y envía los correos mediante el Outbox idempotente. APIs REST y arquitectura realtime compatibles.
- Versión global Dhole 1.36.0. CI y staging pendientes; no desplegar a producción sin autorización.

## [1.21.0] - 2026-11-11

- MINOR DH-20261111-CRM-VISIT-LOCATIONS-096: nuevo registro seguro de ubicaciones para prospectos, con endpoint de consulta/creación/edición, migración PostgreSQL aditiva y eventos existentes de lista de prospectos por outbox.
- DholeWeb 1.34.0 consulta direcciones de cuentas y prospectos y recupera búsqueda Geoapify con fallback. Ecosistema Dhole 1.34.0; pruebas de staging pendientes.

## [1.20.1] - 2026-11-11

- PATCH DH-20261111-CRM-QUOTE-PREFERENCE-REALTIME-094: la preferencia de destinatarios de cotizaciones por cuenta emite invalidación de CRM mediante Outbox/SignalR existente cuando las flags del release realtime están aprobadas; mantiene REST e idempotencia.
- Completa el release 093 con System/global 1.33.1. CI y staging pendientes.

## [1.20.0] - 2026-11-11

- MINOR DH-20261111-CRM-ACCOUNT-QUOTE-DELIVERY-093: configura por cuenta destinatarios de todas las próximas cotizaciones: vendedor únicamente o contacto del cliente con copia al vendedor asignado.
- La preferencia se guarda en CRM, se valida en Pricing en cada envío y se edita sin seleccionar QUO. REST, SignalR, Outbox y el procesamiento idempotente se mantienen.
- Versión global Dhole 1.33.0. CI y staging pendientes; producción no autorizada hasta validación.

## [1.19.0] - 2026-11-11

- MINOR DH-20261111-CRM-GEOAPIFY-VISIT-089: coordenadas geográficas para visitas CRM mediante migración PostgreSQL nullable, validaciones y actualización de endpoints REST. Compatible con registros previos, realtime y DholeWeb 1.31.0.
- Ecosistema Dhole 1.32.0. CI y staging pendientes; despliegue productivo no autorizado.

## [1.18.0] - 2026-11-11

- MINOR DH-20261111-CRM-ACTIVITY-COMPLETE-EDIT-088: endpoint transaccional PUT para edición completa de actividades, minutas, acuerdos y compromisos, incluso finalizadas; autorización por scope y visibilidad, versiones optimistas, auditoría de cambios y recordatorios idempotentes.
- Ampliación sin pérdida de datos de crm_activity_notes a TEXT, compatible con REST y outbox/realtime existentes. Ecosistema Dhole 1.30.0; CI/staging pendientes; producción no autorizada.

## [1.17.1] - 2026-11-11

- PATCH DH-20261111-CRM-RESPONSIBLES-SCOPES-086: los responsables de actividades y seguimientos son usuarios activos habilitados por crm.activity.read, crm.activity.read-all o crm.followup.manage, según los permisos efectivos de Auth. No se exige rol Vendedor. Mantiene autorización para asignar a terceros, REST y SignalR.
- Ecosistema Dhole 1.29.1. CI de master en validación, staging aprobado y publicación productiva autorizada.

## [1.17.0] - 2026-11-11

- MINOR DH-20261111-CRM-LEAD-INSTITUTIONAL-087: backport sobre master para asignar Castro Fallas a prospectos en creación, reasignación y conversión a cuentas con vendedor institucional separado del usuario Auth.
- Incluye migración aditiva y pruebas; conserva registros existentes, REST y SignalR. Ecosistema 1.29.0, CI/staging pendientes, producción no autorizada.

## [1.16.0] - 2026-11-11

- MINOR DH-20261111-CRM-DOCUMENT-DROPZONE-086: mantiene validación de permisos por procedimiento específico, cuenta visible y scopes de edición; compatible con carga múltiple drag & drop en Web 1.28.0.
- Ecosistema Dhole 1.28.0, CRM 1.16.0; mantiene REST, Storage y SignalR, con CI/staging pendientes y producción no autorizada.

## [1.15.0] - 2026-11-11

- MINOR DH-20261111-CRM-PROCEDURE-ATTACHMENTS-085: autorización por procedimiento específico para documentos almacenados en Dhole Storage, con comprobación de visibilidad de la cuenta y permiso crm.account.update.
- Backport selectivo a master del release aprobado en develop/staging, preservando APIs REST, SignalR y releases anteriores.

## [1.14.0] - 2026-11-11

- MINOR DH-20261111-CRM-DOCUMENTS-084: autorización por registro CRM para consultar y adjuntar documentos vinculados a cuentas, prospectos, contactos, actividades y seguimientos; mantiene compatibilidad REST/SignalR y scopes de Storage.
- Ecosistema 1.26.0; compilación, CI y staging pendientes; sin autorización de despliegue a producción.

## [1.13.0] - 2026-11-10

- MINOR DH-20261110-CRM-LEAD-DUPLICATE-083: Bloquea prospectos duplicados entre vendedores por identificación fiscal o identidad comercial; 409 estructurado con propietario desde Auth; bloqueo transaccional, alertas idempotentes en outbox y compatibilidad REST/realtime.
- Versión global Dhole 1.25.0. Validación CI y staging pendiente; sin autorización para despliegue de producción en este cambio.

## [1.12.0] - 2026-11-10

- MINOR DH-20261110-CRM-ACCOUNT-CATEGORY-SCOPE-082: CRM protege la categoría en creación, edición y conversión de prospectos; sin crm.account.category.manage asigna categoría inicial conservadora y rechaza cambios no autorizados.
- Tests CI obligatorios y staging por validar. Merge a master autorizado expresamente.

## [1.11.0] - 2026-11-10

- MINOR DH-20261110-CRM-ACCOUNT-PROCEDURES-080 (master backport): CRUD REST autorizado por cuenta, múltiples procedimientos (nombre, detalle, equipo asignado), concurrencia optimista y migración PostgreSQL aditiva.
- Promoción aislada desde staging del feature 080 para no mezclar cambios de develop 1.24.0. Incluye base opt-in de Outbox/Redis Streams compatible con SignalR (esquema, productor y worker) sin activar flags de despliegue ni alterar contratos REST. Eventos de cuentas por transacción, idempotentes para los consumidores.
- Build y pruebas CI requeridos antes de despliegue automático en producción.

## [1.6.0] - 2026-11-10

- MINOR DH-20261110-CRM-CONTACTS-ROLE-MULTISELECT-079: Validación de responsables con roles de ventas y SuperUsuario activos provenientes de Auth; se conserva autorización por scopes en CRM.
- Se mantienen API REST y realtime existentes. Validación por CI y staging antes de confirmar despliegue.

## [1.5.1] - 2026-11-10

- PATCH DH-20261110-CRM-SELLERS-COMMITMENTS-078: la asignación a otro usuario exige rol Vendedor. Los compromisos vinculados a actividades pueden marcarse completos con crm.activity.update del responsable y autorización de registro, respetando historial antiguo y concurrencia.
- Ecosistema 1.20.1; CI y staging pendientes. REST y realtime sin cambios de contrato.

## [1.5.0] - 2026-11-10

- MINOR DH-20261110-CRM-AGENDA-UNIFIED-077: guarda minuta detallada y contactos participantes de actividades, admite creación para otros vendedores autorizados y filtra calendarios por vendedor; conserva lectura de agendas con crm.account.read-all y crm.lead.read-all.
- Sin migraciones de base ni cambios en contratos realtime; CI y staging previos a la promoción a master.

## [1.4.7] - 2026-11-10

- PATCH DH-20261110-CRM-GLOBAL-AGENDAS-076: lectura y creación de actividades de otros usuarios con crm.activity.read-all o ambos scopes crm.account.read-all y crm.lead.read-all; directorio Auth interno protegido para vendedores activos, preservando controles por registro, uso de usuarios activos y REST.
- Ecosistema Dhole 1.19.5; CI y staging pendientes, sin despliegue de producción.

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
