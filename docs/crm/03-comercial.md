# DholeCRM — Fase 3: Comercial y ejecución de pruebas

Basado exclusivamente en `CODEX_DHOLECRM_IMPLEMENTACION_COMPLETA.md`. Dependencia de Fases 1 y 2: los PR #1 y #2 de CRM, #12 de Auth y #7 de Config **todavía deben integrarse primero**.

## Alcance incorporado
- Pipeline configurable con etapas, probabilidad y cierre ganado/perdido.
- Oportunidades con un sujeto (cuenta o prospecto), ejecutivo verificado en Auth, moneda validada en Config, búsqueda/paginación, movimientos, cierres/reaperturas y trazabilidad de cambios.
- Contactos de decisión y negociación manual; **sin enlazar cotizaciones de Pricing**, responsabilidad de Fase 4.
- Actividades comerciales (visita, llamada, correo, reunión, otra), agenda de fechas UTC, participantes, observaciones, reprogramaciones, ejecución y cancelación con histórico.
- Análisis comercial con múltiples rutas POL/POE/POD, mercado, aduanas, servicios, modalidades, volumen/unidad y perfiles comerciales de cuentas; sin crear ni requerir flete internacional.
- 15 nuevos scopes idempotentes en Auth; 4 nuevos grupos de catálogos y 16 ítems iniciales en Config.

## Esquema nuevo
Migración `20261014210000_AddCommercialPipelineActivitiesAnalysis`, **13 tablas aditivas** bajo `crm`, además de las 10 de la Fase 2. Restricciones PostgreSQL de importes, porcentajes, fechas, entidad padre, pertenencia etapa↔pipeline e historial inmutable al no habilitar DELETE.

El migrador one-shot acepta únicamente migraciones declaradas de Fases 2 y 3 y exige `CRM_SCHEMA_MIGRATION_APPROVED=true`, credenciales protegidas y respaldo completo con `pg_dump`, validado antes de aplicar en el runner. Nunca migrar automáticamente al arrancar API/worker; nunca hacer rollback destructivo de BD.

## Puertas de promoción
CI de pull request sobre .NET 10, PostgreSQL 17, Redis, contratos gRPC existentes, Docker y controles de seguridad. El interruptor backend `Features__CrmEnabled` continúa desactivado por defecto. No integrar ni desplegar Fase 3 sin que Fases 1 y 2 funcionen en staging, migraciones probadas, autenticación y Gateway seguros. La UI (Fase 7), integración Pricing (Fase 4), recordatorios/envío de avisos/Outbox (Fase 5) y crédito (Fase 6) quedan fuera de esta entrega. No se debe interpretar éxito CI como despliegue.

## Operación
Para contabilizar ventas por etapa usar `crm_opportunities` filtrando por ejecutivo/estado y utilizar `crm_opportunity_stage_history` para auditoría temporal; valores monetarios no se sustituyen por costos de Pricing. `crm_activities` mantiene visitas, llamadas y participantes; completar no envía correos ni crea recordatorios en esta fase. Las observaciones se agregan, no se eliminan. Los permisos requieren scope y pertenencia del registro verificada por backend.
