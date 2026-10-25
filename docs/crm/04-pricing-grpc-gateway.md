# DholeCRM — Fase 4: contratos y vínculo seguro con Pricing

Fuente funcional: `CODEX_DHOLECRM_IMPLEMENTACION_COMPLETA.md`.

## Responsabilidades verificadas

- `DholeCRMService` almacena solamente vínculos lógicos a cotizaciones y solicitudes y snapshots comerciales. El importe/costo, la aprobación interna y los estados reales permanecen en Pricing.
- `DholePricingService` expone dos resúmenes estrictamente filtrados: `GET /api/pricing/crm/requests/{id}` (requiere scope Pricing request-create y visibilidad de vendedor) y `GET /api/pricing/crm/quotes/{id}` (requiere rate-view más visibilidad de vendedor, sin costos).
- `DholeApiGateway` enruta `/api/crm/*` a CRM sin quitar prefijo; requiere JWT y `Features__CrmGatewayEnabled=true`. El propio CRM vuelve a autorizar scopes y propiedad; `Features__CrmEnabled` sigue desactivado por defecto.
- No se inventó un servicio gRPC de Pricing: su servicio real usa Minimal API HTTP. CRM sirve gRPC `CrmAccountReadService` y `CrmOpportunityReadService` (v1) con identidad interna, actor activo y ACL individual.

## Solicitud y cotización

El vendedor inicia `POST /api/crm/opportunities/{id}/rate-requests` con `Idempotency-Key`. CRM deriva vendedor y cliente reales de la oportunidad, valida propiedad, usa el JWT original al enviar la solicitud al endpoint existente de Pricing y verifica el identificador real. Pricing mantiene su propio idempotency middleware. CRM guarda correlación, fingerprint, estado y referencia; repetir la clave con otro contenido responde 409.

Para vincular manualmente una solicitud existente: `POST /api/crm/opportunities/{id}/rate-requests/link`; para asociar una tarifa existente: `POST /api/crm/opportunities/{id}/quotes`. No se aceptan referencias sin verificación de propiedad y existencia por el endpoint real de Pricing. `GET` devuelve registros vinculados; `POST /refresh` consulta el estado verdadero y actualiza snapshots en BD. Un refresh de solicitud asocia automáticamente la tarifa que Pricing ya tiene vinculada, si existe.

Se admite más de una cotización por oportunidad con unicidad por par `(opportunity_id,pricing_quote_id)`. La solicitud de Pricing se vincula como máximo a una oportunidad. Un estado `AcceptedByClient` **NO** marca la oportunidad CRM como ganada. Una aprobación de margen o la mera emisión tampoco la cierra.

## Migraciones y despliegues

`20261014220000_AddPricingCommercialLinks` crea `crm_opportunity_quotes` y `crm_opportunity_pricing_requests`; índices y restricciones son aditivos y no alteran tablas propias de Pricing. El migrador CRM acepta únicamente migraciones conocidas Fases 2,3,4 y requiere aprobación expresa de entorno, backup `pg_dump` comprobado y rollback de imagen sin destruir datos.

Para staging se requieren previamente integración y despliegue de Fases 1–3, revisión del Compose efectivo que incluye el archivo del servidor y habilitación de rutas/autenticación en los respectivos servicios. Antes de promover se debe ejecutar el flujo completo vendedor → crear solicitud Pricing → vincular tarifa → actualizar estado real y comprobar que la oportunidad CRM mantiene estado manual.

**Pendiente de Fase 5:** consumidor asíncrono Redis Streams/Outbox-Inbox para reflejar cambios automáticamente en segundo plano, recordatorios y notificaciones. En Fase 4 existe actualización autenticada mediante `refresh`; no se simula un evento ni un worker activo.
