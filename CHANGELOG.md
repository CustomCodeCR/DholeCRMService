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
