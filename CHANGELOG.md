## [1.27.1] - 2026-11-12

- PATCH DH-20261112-CRM-BITACORA-PRODUCTION-127: publicación autorizada a producción de actividades CRM sin agenda y recordatorios explícitos, sin modificaciones de API ni esquema frente al release 126 validado en staging. CRM 1.27.0 → 1.27.1, Web 1.47.0 → 1.47.1, ecosistema 1.47.0 → 1.47.1.
- Staging y CI deberán validarse antes de promover el mismo release a master; producción autorizada expresamente.

## [1.27.0] - 2026-11-12

- MINOR DH-20261112-CRM-BITACORA-REMINDERS-126: registros comerciales sin fechas programadas se guardan como Completed en el instante de creación, nunca como Scheduled sin inicio ni se encolan avisos.
- Conserva registros históricos, actividades futuras y sus recordatorios, permisos, notas, adjuntos, REST y SignalR/Outbox sin migración.
- Prueba para actividades sin agenda; CRM 1.26.0 → 1.27.0, Web 1.46.0 → 1.47.0, ecosistema 1.46.0 → 1.47.0. CI y staging pendientes, producción no autorizada.

## [1.26.0] - 2026-11-12

- MINOR DH-20261112-CRM-ACTIVITY-TYPES-125: actividades CRM admiten Seguimiento comercial, WhatsApp, Recomendación y Oferta; CALL conserva su código y recordatorio de llamada telefónica. Se mantienen las cinco clasificaciones históricas y el recurso FOLLOWUP separado.
- La validación de creación/edición acepta los códigos del catálogo Config; recordatorios genéricos usan el canal realtime/Outbox existente. Pruebas unitarias para códigos válidos y rechazo de desconocidos.
- DholeCRMService 1.25.3 → 1.26.0; DholeConfigService 1.5.0; DholeWeb 1.46.0; ecosistema 1.46.0. CI y staging pendientes; producción no autorizada.

## [1.25.3] - 2026-11-12

- PATCH DH-20261112-CRM-REMINDER-CONTEXT-124: correos de actividades y seguimientos CRM muestran Cliente o Prospecto con nombre actual, incluso cuando la relación viene de la oportunidad comercial; notificaciones de supervisión conservan vendedor, compromiso y vencimiento.
- Añade enlace HTML seguro y directo al detalle `/crm/activities/{id}` o `/crm/followups/{id}`, con URL pública configurable por entorno; las notificaciones internas mantienen texto plano y reciben ruta segura en el payload para navegación existente.
- Pruebas unitarias de nombres y escape HTML, rutas cliente/prospecto, URL de staging y bloqueo de esquemas externos. Sin cambios de esquema, REST, SignalR ni claves de deduplicación/outbox.
- DholeCRMService 1.25.2 → 1.25.3; DholeSystem/ecosistema 1.44.2 → 1.44.3. CI, compilación, pruebas y staging pendientes; no se autoriza despliegue a producción.

## [1.25.2] - 2026-11-12

- PATCH DH-20261112-PRICING-CRM-LEAD-OWNER-120: corregido endpoint GET /pricing-client-options; prospectos usan su IsCastroFallasExecutive real, sin fijarlo en false. Pricing muestra al responsable institucional Castro Fallas aun cuando ExecutiveUserId sea el usuario operativo que creó o custodia el prospecto.
- Se conserva filtrado por scopes y ACL existentes, paginación, búsqueda, API REST y SignalR/Outbox. Se agrega test de regresión del contrato de proyección institucional.
- CRM 1.25.1 → 1.25.2 y DholeSystem/ecosistema 1.43.7 → 1.43.8. DholeWeb 1.43.6 y PricingService 1.16.3 sin cambios. Pendiente validación en staging de TARIFARIO y de borradores de Pricing antes del despliegue a producción.

## [1.25.1] - 2026-11-12

- PATCH DH-20261112-CRM-PRODUCTION-GATE-113: elimina la condición de exclusión en el workflow de producción CRM que causó el estado Skipped del release de agrupación de cuentas.
- Restaura los despliegues automáticos con push a master y conserva workflow_dispatch, migraciones versionadas, health checks, REST y SignalR/Outbox.
- El modelo de agrupación de cuentas permanece intacto; CRM 1.25.0 → 1.25.1, ecosistema 1.41.2, DholeWeb 1.41.1 sin modificaciones. Despliegue CRM autorizado en esta conversación.

## [1.25.0] - 2026-11-12

- MINOR DH-20261112-CRM-ACCOUNT-GROUPS-112: agrupación comercial de cuentas bajo una cuenta principal, manteniendo la identidad fiscal, vendedor, contactos y contratos de cada cuenta.
- Endpoints REST GET /accounts/{id}/group, POST /accounts/{id}/group/members y DELETE /accounts/{id}/group/members/{memberId}, con control de versión, ACL y prevención de jerarquías circulares.
- Consultas compartidas para actividades, seguimientos, análisis comercial, procedimientos específicos, tarifas preferenciales, rutas y oportunidades/cotizaciones CRM. Conserva el productor realtime/outbox existente.
- DholeCRMService 1.24.1 → 1.25.0; DholeWeb → 1.41.0; ecosistema 1.41.0. Validaciones CI y staging pendientes; sin despliegue autorizado a producción.

## [1.24.1] - 2026-11-12

- PATCH DH-20261112-CRM-DRAFT-DHMODAL-111: versión de CRM coordinada con la corrección frontend de recuperación de borradores mediante DhModal, sin confirmaciones nativas.
- Sin cambios en endpoints, esquema, migraciones, contratos, scopes ni SignalR. Requiere DholeWeb 1.40.1 y manifiesto global 1.40.1.

## [1.24.0] - 2026-11-11

- MINOR DH-20261111-CRM-DRAFT-RECOVERY-109: versión coordinada de CRM con el nuevo mecanismo de recuperación de formularios de DholeWeb 1.39.0. Sin cambios a endpoints, modelo de datos o comportamiento de API: los borradores se conservan localmente en Web hasta confirmar la operación CRM por REST, manteniendo los canales realtime actuales.
- DholeCRMService 1.23.0 → 1.24.0; ecosistema 1.39.0. CI y staging pendientes, sin autorización para despliegue en producción.

## [1.23.0] - 2026-11-11

- MINOR DH-20261111-CRM-ACCOUNT-INLINE-RELATIONS-107: permite registrar contactos y direcciones opcionales al crear una cuenta en una sola transacción EF Core. Incluye permisos, validación de país, contactos principales y jerarquías geográficas, sin alterar las APIs REST existentes.
- La creación de cuentas sin contactos ni direcciones conserva su funcionamiento previo. CRM 1.22.0 → 1.23.0; versión global del ecosistema Dhole 1.38.0. CI y staging pendientes; no se autoriza despliegue a producción.

## [1.22.0] - 2026-11-11

- MINOR DH-20261111-CRM-QUOTE-MULTI-CONTACTS-102: configuración por cuenta de todos los destinatarios de cotizaciones futuras. Elimina el selector de QUO y permite elegir varios contactos con DhMultiSelect; se preservan las opciones solo vendedor o cliente con CC vendedor asignado.
- CRM guarda y valida la lista y migra el contacto único anterior; Pricing comprueba todos los contactos antes de preparar el PDF y envía los correos mediante el Outbox idempotente. APIs REST y arquitectura realtime compatibles.
- Versión global Dhole 1.35.0. CI y staging pendientes; no desplegar a producción sin autorización.

## [1.21.0] - 2026-11-11

- MINOR DH-20261111-CRM-VISIT-LOCATIONS-096: ubicaciones registradas de prospectos, endpoint REST protegido por scopes y titularidad, migración aditiva crm_lead_addresses, coordenadas opcionales y notificación de cambios de prospecto mediante outbox existente.
- Selector de ubicaciones desde clientes y prospectos en DholeWeb 1.34.0, geocodificación Geoapify con fallback de búsqueda y mensajes de diagnóstico. Ecosistema 1.34.0; pendiente validación integrada y staging.

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

## [1.17.0] - 2026-11-11

- MINOR DH-20261111-CRM-LEAD-INSTITUTIONAL-087: permite seleccionar Castro Fallas como vendedor institucional de prospectos y reasignarlos, manteniendo un Auth responsable, scopes y auditoría.
- Migración aditiva del indicador, listado y duplicados con etiqueta institucional, conversión segura de prospecto a cuenta; conserva cambios de responsables por scopes de 1.16.1, REST y SignalR. Ecosistema Dhole 1.29.0. CI/staging pendientes, producción no autorizada.

## [1.16.1] - 2026-11-11

- PATCH DH-20261111-CRM-RESPONSIBLES-SCOPES-086: responsables de actividades y seguimientos validados mediante scopes efectivos de Auth (crm.activity.read, crm.activity.read-all, crm.followup.manage) sin exigir rol comercial; preserva controles de delegación y corrige comprobaciones nulas de calendario.
- Ecosistema 1.28.1, REST/SignalR compatibles; CI/staging pendientes, sin autorización productiva.

## [1.16.0] - 2026-11-11

- MINOR DH-20261111-CRM-DOCUMENT-DROPZONE-086: soporte autorizado de documentos por procedimiento y su cuenta, compatible con drag & drop sin límite fijo de archivos; conserva REST/SignalR y permisos CRM.
- Ecosistema Dhole 1.28.0; DholeWeb 1.28.0. CI y staging pendientes, producción no autorizada.

## [1.15.0] - 2026-11-11

- MINOR DH-20261111-CRM-PROCEDURE-ATTACHMENTS-085: autorización CRM de documentos específicos por procedimiento y cuenta visible, sin exponer archivos de cuentas ajenas; validación del permiso crm.account.update para carga en Storage.
- Reutiliza endpoints REST y Outbox/SignalR existentes. Ecosistema 1.27.0; CI/staging pendientes y despliegue productivo no autorizado.

## [1.14.0] - 2026-11-11

- MINOR DH-20261111-CRM-DOCUMENTS-084: autorización por registro CRM para consultar y adjuntar documentos vinculados a cuentas, prospectos, contactos, actividades y seguimientos; mantiene compatibilidad REST/SignalR y scopes de Storage.
- Ecosistema 1.26.0; compilación, CI y staging pendientes; sin autorización de despliegue a producción.

## [1.13.0] - 2026-11-10

- MINOR DH-20261110-CRM-LEAD-DUPLICATE-083: Bloquea prospectos duplicados entre vendedores por identificación fiscal o nombre comercial, devuelve HTTP 409 con vendedor asignado, protección transaccional y aviso durable por outbox.
- Ecosistema Dhole 1.25.0; validaciones CI y staging pendientes. Sin despliegue de producción aprobado.

## [1.12.0] - 2026-11-10

- MINOR DH-20261110-CRM-ACCOUNT-CATEGORY-SCOPE-082: El API de CRM impide cambiar la categoría sin crm.account.category.manage, incluso en solicitudes directas y conversiones; el alta sin permiso asigna nivel inicial seguro.
- CI y validación en staging pendientes; sin despliegue de producción autorizado.

## [1.11.0] - 2026-11-10

- MINOR DH-20261110-CRM-ACCOUNT-PROCEDURES-080: registra múltiples procedimientos específicos por cuenta con nombre, detalle y equipo asignado, CRUD REST autorizado por cuenta y concurrencia optimista.
- Migración PostgreSQL aditiva; las mutaciones reutilizan la invalidación duradera de la cuenta por Outbox/SignalR cuando está habilitada. Requiere validación CI y staging antes de promover a master.

## [1.10.0] - 2026-11-10

- MINOR DH-20261110-CRM-CONTACTS-SELLERS-078: la validación de asignación comercial admite roles activos Vendedor, Vendedores, Vendedor Supervisor, Vendedor Jefe y SuperUsuario de Auth. Complementa las verificaciones de vendedor ya existentes en 1.9.3.
- Preserva acceso de lectura, actividades previas, endpoints REST, SignalR y notificaciones.

## [1.9.3] - 2026-11-10

- PATCH DH-20261110-CRM-SELLERS-COMMITMENTS-078: exige rol Vendedor para asignar actividades y seguimientos a otros usuarios. Permite completar compromisos vinculados a la actividad propia usando crm.activity.update, incluidos vínculos históricos confirmados en el historial de creación; conserva permisos, concurrencia y auditoría.
- Release global 1.20.1; contratos REST existentes y realtime conservados; CI y staging pendientes.

## [1.9.2] - 2026-11-10

- PATCH DH-20261110-CRM-GLOBAL-AGENDAS-076: autoriza consultar agendas y crear actividades de otros vendedores con crm.activity.read-all o la combinación crm.account.read-all + crm.lead.read-all; mantiene la verificación de clientes/prospectos, usuarios activos y permisos de escritura. Habilita directorio interno protegido para estos scopes.
- Ecosistema 1.19.5; pruebas de compilación, CI y staging pendientes.

## [1.9.1] - 2026-11-10

- PATCH DH-20261110-CRM-HISTORICAL-ACTIVITIES-075: permite registrar actividades CRM con fechas anteriores, conservando fechas UTC y marcando como completadas las que ya finalizaron. Las actividades completadas no generan recordatorios retroactivos. Mantiene reglas de fechas futuras de compromisos y seguimientos, permisos, minutas y las APIs existentes.
- Coordinado con DholeWeb/DholeSystem 1.19.4; sin migraciones de datos ni cambios de SignalR. CI y validación en staging pendientes.

## [1.9.0] - 2026-11-10

- MINOR DH-20261110-CRM-AGENDA-RECONCILED-070: compromisos asociados a actividad principal, lectura retrocompatible, reapertura y seguimiento de estado; contactos y minuta en actividades CRM.
- Migración PostgreSQL aditiva; conserva las correcciones de CRM-PROSPECT-SELLER-072. Web/System 1.19.0; CI y staging pendientes.

## [1.8.1] - 2026-11-10

- DH-20261110-CRM-PROSPECT-SELLER-072: endpoint GET /leads/assigned-executives, resuelve nombres de vendedores de prospectos mediante Auth y verifica permisos de lectura por cada registro; respeta read-all y no expone prospectos ajenos.
- Sin migración de base de datos; creación y reasignación ya admiten executiveUserId y crm.lead.assign. Ecosistema Dhole 1.18.2.

## [1.8.0] - 2026-11-10

- MINOR DH-20261110-CRM-ADMIN-CALENDAR-069: permite a administradores activos consultar todas las agendas de CRM y los seguimientos automáticos, con detalles históricos y directorio filtrado por Auth. Lectura ampliada con roles vigentes, sin alterar autorización de escritura.
- Conserva íntegramente CRM 1.7.6 de rutas y cotizaciones comerciales de prospectos; Auth 1.6.0, Web/System 1.18.0. CI y staging pendientes.

## [1.7.6] - 2026-11-10

- DH-20261110-CRM-PROSPECT-ROUTES-QUOTES-069: normalización UPPER CASE de nombres de prospectos en creación y edición, endpoints autorizados de oportunidades y cotizaciones vinculadas al prospecto.
- Release global Dhole 1.17.13, sin migraciones.

## [1.7.5] - 2026-11-10

- PATCH DH-20261110-PRICING-CRM-REQUEST-IDENTITY-067: el selector autorizado de cuentas y prospectos permite resolver selectedClientId desde solicitudes existentes, sin perder filtros de permisos por cuenta, prospecto o vendedor y sin paginar fuera de su alcance. Release global Dhole 1.17.12, CI y staging pendientes.

## [1.7.4] - 2026-11-10
- PATCH DH-20261110-CRM-ACTIVITY-FULL-DETAIL-066: API de solo lectura para recuperar los compromisos creados desde actividades y la actividad principal de cada seguimiento derivado. Reconstrucción de vínculos mediante historial histórico existente, sin migraciones ni pérdida de datos.
- Respeta autorización CRM por propietario, cuenta y actividad. Endpoints GET /activities/{id}/commitments y GET /followups/{id}/parent-activity; versiones DholeWeb/DholeSystem 1.17.11; pendientes CI y staging.

## [1.7.3] - 2026-11-10
- PATCH DH-20261110-CRM-ACCOUNT-SEARCH-064: GET /api/crm/accounts busca por nombre comercial, razón social, cédula o identificación fiscal parcial y completa, con y sin guiones, puntos o espacios.
- Se respeta la autorización original, los filtros y la paginación SQL. Incluye registros históricos sin identificación normalizada, sin migraciones ni cambios a SignalR/REST. Ecosistema 1.17.9, candidato pendiente de CI/staging; sin autorización de producción.

## [1.7.2] - 2026-11-09
- PATCH: add authenticated read-scope GET /api/crm/accounts/realtime-access and /api/crm/leads/realtime-access. REST endpoints return no customer or lead fields.
- Durable transactional accountlist/leadlist invalidations for accounts and lead create/update/assign/archive/convert via existing CRM outbox and NotificationsHub, no new hub. Staging Compose sourced from DholeSystem/develop.
- Ecosystem 1.16.1; master/production unchanged.

## [1.7.0] - 2026-11-09
- Nuevo buscador unificado de cuentas y prospectos para cotizaciones de Pricing, limitado a registros CRM visibles y con vendedor asignado desde Auth.

# Changelog

## [1.6.0] - 2026-11-09 (NotificationsHub universal realtime pilot; candidate)

- MINOR: retain domain Outbox/REST producer and scoped access under the existing NotificationsHub delivery model; CustomCodeFramework untouched.
- Ecosystem candidate 1.14.0 / 16 Dhole repositories, release `rt-20261107-01` in isolated branch `feature/rt-notificationshub-20261109-01`.
- Staging deployment, operational audit and final integration remain pending. Production flags OFF.


## [1.5.6] - 2026-11-08 (Dhole realtime coordinated hardening)

- PATCH alignment to ecosystem 1.13.15 / rt-20261107-01; F2/F6 read-only AOF attestation and F10 branch retention hardening in DholeSystem.
- Candidate unchanged in functionality; all realtime flags remain OFF; F6/F7/F8 formal gates and F9/F10 promotion pending.



## [1.5.5] - 2026-11-08 (F9 candidate, F3 WSS review waiver)

- Incremento PATCH del release global 1.13.14 por política F9 de revisión de evidencia WSS ya aprobada en CI. No implica aprobar F3 staging, activar realtime ni desplegar. Sin cambios de negocio en este componente.




## [1.5.4] - 2026-11-08 (realtime F3, release candidato)

- Se sincroniza SemVer PATCH y la versión global Dhole 1.13.13 por el nuevo ensayo automático TLS/WSS con Traefik en DholeApiGateway. Sin cambios funcionales adicionales en este componente, sin merge ni despliegue.


## [1.5.3] - 2026-11-08 (F6/F9 staging-audit candidate)

- Sincroniza Dhole Realtime `rt-20261107-01` a versión global `1.13.12` tras incorporar auditoría real de persistencia en staging. PATCH 1.5.2 → 1.5.3; no modifica rutas, eventos, datos, contratos REST ni flags. No se fusiona hasta completar gates formales.



## [1.5.2] - 2026-11-08 (candidato F9, sin publicar)

- Sincroniza release global `rt-20261107-01` a `1.13.11` tras resolver los hallazgos de CI de Config, Reports y Gateway. Bump PATCH de DholeCRMService `1.5.1` a `1.5.2`, sin modificar REST, eventos, precios ni datos; flags realtime OFF y PR F9 aún DRAFT.



## [1.5.1] - 2026-11-08 (candidato F9, sin publicación)

- Sincroniza metadatos de versión global del batch Dhole Realtime `rt-20261107-01` a `1.13.10`; actualización PATCH independiente de DholeCRMService `1.5.0` → `1.5.1`. No cambia contratos REST, datos, lógica de negocio ni flags; requiere gates F0–F8 y CI de SHA final antes de merge.


- F7 C08 (candidato NO desplegado): 10 carreras de escritura entre dos clientes TestServer con mismo actor, PostgreSQL real, HTTP 409 para revisión obsoleta, una salida Outbox por versión y cero duplicados; CI #42511869179 PASS. No equivale a usuarios distintos ni staging real; versión 1.5.0 sigue candidata sin publicación.


## [1.5.0] - 2026-11-08 (candidato F6.01, SIN PUBLICAR)

- F6.01: añade invalidaciones realtime transaccionales para crear cuentas, registrar actividad comercial de cuenta, actualizar logo, archivar y reasignar cuentas CRM; una operación de negocio y su evento Outbox se confirman juntos con permisos existentes.
- Cada evento utiliza un tipo de la allowlist y una versión monotónica de recurso; no contiene campos del cliente. El publicador sigue apagado hasta aprobación explícita del release.
- Pruebas de operaciones HTTP reales de CRM con PostgreSQL y TestServer para prevenir pérdida/duplicación de eventos. Requiere cobertura completa de mutaciones, reconciliación WSS y revisión de gates para aprobar F6.01.
- Versión de componente 1.4.0 -> 1.5.0 en rama intermedia, versión global candidata 1.13.0 sin publicar; no modifica develop/master ni producción.


## [1.4.0] - 2026-11-07 (candidato aislado, NO liberado)

- Preparación técnica F5, sin gate aprobado: el PATCH real de cuenta CRM persiste actualización, historial y evento mínimo `crm.account.updated.v1` en una sola transacción PostgreSQL bajo flags de release.
- El publicador exige además `Realtime:BatchApproved` y `Realtime:ReleaseId=rt-20261107-01`; pruebas de 20 actualizaciones reales con 20 registros Outbox, sin exposición de campos personales.
- F5 permanece BLOQUEADA hasta gates F0-F4, permisos reales, WSS/Traefik y prueba de dos usuarios con reconexión/rollback en staging; la release sigue sin desplegar.
- F2 piloto de durabilidad de realtime: tablas PostgreSQL separadas `crm_realtime_outbox/inbox` con índices, leases recuperables y DLQ; la infraestructura original de recordatorios CRM permanece intacta.
- Productor transaccional y publicador Redis Streams con reintentos exponenciales y jitter; consumo de prueba idempotente con ACK después del commit.
- Worker registrado **apagado por defecto**; requiere `Features:UniversalRealtimeEnabled=true` y `Realtime:Outbox:Enabled=true` tras completar F0-F10 y aprobación de batch.
- Pruebas de F2 aisladas: 11/11 PASS con PostgreSQL 17/Redis 7 y ASP.NET TestServer real en run 42485087251 (SHA ec4bf2c01b4d4a84eff4a1358187b6ac37c4ec78). Confirma HTTP 201 y Outbox transaccional pese a fallo simulado del publicador Redis y GET 200 posterior. No equivale a prueba de endpoints CRM comerciales en staging.
- No considerar F2 aprobada hasta evidencia operativa de REST real, AOF/backup/retención, recuperación y seguridad en staging y F0/F1 aprobados.
- Ningún cambio directo a `develop`, `master`, `main`, ni despliegue.



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
