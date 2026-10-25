-- Supervisors run the quarterly and annual reviews, so they read attendance rates (verified events only; still no personnel decision).
-- Technical Trainer: schedules and delivers training that new hires, expiring qualifications, supervisors and approved development
-- packets ask for, and is notified when a request needs scheduling. Certification remains with Certifiers and Training Coordinators.
INSERT INTO kernel.permission_catalog(permission, description)
VALUES
    ('workforce.training-requests.read', 'Read training requests and new-employee training checklists'),
    ('workforce.training-requests.request', 'Ask for training to be scheduled for a person'),
    ('workforce.training-requests.manage', 'Schedule, complete or cancel training requests'),
    ('workforce.onboarding.manage', 'Define the training every new employee must complete')
ON CONFLICT DO NOTHING;

INSERT INTO kernel.role_definition(role_name, display_name)
VALUES ('TechnicalTrainer', 'Technical Trainer')
ON CONFLICT (role_name) DO UPDATE SET display_name = EXCLUDED.display_name;

INSERT INTO kernel.role_permission(role_name, permission)
VALUES
    ('TechnicalTrainer', 'workforce.read'),
    ('TechnicalTrainer', 'competency.read'),
    ('TechnicalTrainer', 'resources.read'),
    ('TechnicalTrainer', 'workforce.training-requests.read'),
    ('TechnicalTrainer', 'workforce.training-requests.request'),
    ('TechnicalTrainer', 'workforce.training-requests.manage'),
    ('TechnicalTrainer', 'workforce.onboarding.manage'),
    ('TechnicalTrainer', 'notifications.read'),
    ('TechnicalTrainer', 'notifications.acknowledge'),
    ('TechnicalTrainer', 'kernel.delegations.propose'),
    ('TrainingCoordinator', 'workforce.training-requests.read'),
    ('TrainingCoordinator', 'workforce.training-requests.request'),
    ('TrainingCoordinator', 'workforce.training-requests.manage'),
    ('TrainingCoordinator', 'workforce.onboarding.manage'),
    ('Supervisor', 'workforce.attendance-intelligence.read'),
    ('Supervisor', 'workforce.training-requests.read'),
    ('Supervisor', 'workforce.training-requests.request'),
    ('HumanResources', 'workforce.training-requests.read'),
    ('HumanResources', 'workforce.training-requests.request')
ON CONFLICT DO NOTHING;

-- One in-app alert per request that needs scheduling, cleared when it is scheduled, completed or cancelled.
INSERT INTO kernel.notification_rule
    (rule_id,name,active,trigger_event_type,clear_event_type,scope_type,scope_id,condition_field,condition_equals,severity,confidence,
     recipient_roles,escalation_roles,channels,template,resolution_template,deep_link_template,delay_seconds,reminder_seconds,escalation_seconds,
     quiet_start,quiet_end,time_zone,dedup_seconds,created_at,created_by,updated_at,updated_by)
VALUES
    ('0b1d5a57-0011-4a02-9a02-000000000001','Training needs scheduling',true,'TrainingRequested','TrainingRequestHandled','Global',NULL,NULL,NULL,
     'ActionRequired','Confirmed',ARRAY['TechnicalTrainer'],ARRAY['TrainingCoordinator'],ARRAY['InApp'],
     '{message}',
     'The training request was scheduled, completed or cancelled.','#/performance?training={entityId}',0,86400,259200,NULL,NULL,'UTC',86400,now(),'system:migration',now(),'system:migration')
ON CONFLICT DO NOTHING;
