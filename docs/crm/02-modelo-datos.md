# DholeCRM — Modelo de datos y límites de Fase 2

Documento de implementación basado en `CODEX_DHOLECRM_IMPLEMENTACION_COMPLETA.md`; NO sustituye la especificación funcional ni acredita despliegue.

## Dueños de información

CRM es dueño del **perfil comercial** de cuentas, prospectos, conversiones, contactos, direcciones y preferencias de comunicación si no hay dueño duplicado en el entorno validado. Auth sigue siendo la única fuente de usuarios, scopes y roles: CRM almacena UUID lógicos, nunca credenciales ni copias de la tabla Users. Config mantiene países, monedas y catálogos; CRM guarda códigos/UUID lógicos. No hay FKs entre bases de servicios.

## Tablas versión 20261014193000_InitialCrmData

1. `crm_accounts`: datos de cuenta, categoría de cliente/NVOCC, ejecutivo y datos fiscales normalizados.
2. `crm_leads`: prospectos y su estado de calificación/conversión.
3. `crm_lead_conversions`: trazabilidad de conversión, una sola por lead.
4. `crm_account_assignments`: historial de asignación con una única asignación activa.
5. `crm_contacts`: datos de contactos.
6. `crm_account_contacts`: relaciones N:M y contacto principal por cuenta.
7. `crm_addresses`: direcciones sin coordenadas obligatorias.
8. `crm_contact_preferences`: canal, días, zona horaria, horario y do-not-contact.
9. `crm_marketing_consents`: historial append-only, evidencia para opt-in y revocaciones.
10. `crm_account_business_activities`: códigos de actividad y origen de verificación.

La migración es **aditiva** y usa constraints / índices de identidad única, asignaciones activas y contacto principal. Cada `CrmRecord` conserva `created_at`, `updated_at`, `created_by_user_id`, `updated_by_user_id` y `row_version`. `Down` arroja excepción para no eliminar datos históricos.

## API, seguridad y límites

- Endpoints CRUD bajo `/api/crm/accounts`, `/api/crm/leads`, `/api/crm/contacts`, más subrutas de asignaciones, conversiones, contacto, preferencias, consents y direcciones.
- La identidad del ejecutor proviene de JWT firmado y validación de usuario activo en Auth con clave interna. El backend exige scopes efectivos y propiedad del registro; envío de executive_user_id no otorga permisos.
- Referencias a país se verifican por el gRPC de Config existente, con deadline; ausencia de Config/Auth bloquea la operación.
- Clientes `cash`/`credit` describe tipo comercial, **no aprobación de crédito**; esa lógica corresponde a Fase 6.
- Datos personales y marketing no se emiten a trabajadores ni a eventos en esta fase; consentimientos requieren evidencia para OPTED_IN.
- Config crea 6 grupos/24 opciones de Fase 2; se conserva y se reutiliza el catálogo corporativo previamente existente.

## Verificación y despliegue

CI valida compilación, contratos gRPC, tests con PostgreSQL 17/Redis y Docker. Migración requiere previamente una copia pg_dump validada, una autorización de entorno y una ejecución de staging demostrablemente sana. El PR se construyó sobre la rama Fase 1 por ser requisito técnico; **NO fusionar hasta integrar primero la Fase 1 y completar revisión del Compose efectivo**. El marcador de versión y rollback solo de imágenes preserva la base de datos.

Pendiente fuera de Fase 2: gestión de oportunidades, seguimiento, Pricing, UI, Hacienda, Dynamics y envío de comunicaciones. No hay despliegue acreditado aquí.
