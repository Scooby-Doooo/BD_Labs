\set ON_ERROR_STOP on
\encoding UTF8
BEGIN;
-- id 1..5
INSERT INTO users(email,display_name) VALUES
 ('client@example.com','Олена Замовник'),('developer@example.com','Андрій Розробник'),
 ('designer@example.com','Марія Дизайнер'),('other@example.com','Інший Замовник'),
 ('both@example.com','Користувач з двома ролями');
INSERT INTO roles(code,name) VALUES ('client','Замовник'),('freelancer','Фрілансер'),('admin','Адміністратор');
INSERT INTO user_roles VALUES (1,1),(2,2),(3,2),(4,1),(5,1),(5,2);
INSERT INTO client_profiles VALUES (1,'Навчальна компанія','Вебпроєкти'),(4,NULL,''),(5,NULL,'');
INSERT INTO freelancer_profiles VALUES (2,'C# / PostgreSQL developer','Розробка баз даних',500),
 (3,'UI designer','Дизайн інтерфейсів',450),(5,'Full stack','',600);
INSERT INTO categories(name) VALUES ('Веброзробка'),('Дизайн'),('Бази даних');
INSERT INTO skills(name) VALUES ('C#'),('PostgreSQL'),('UI/UX'),('HTML/CSS');
INSERT INTO freelancer_skills VALUES (2,1),(2,2),(3,3),(3,4),(5,1),(5,4);
-- Решту даних створюємо через процедури
DO $$ DECLARE a bigint; b bigint; c bigint; pr bigint; co bigint; mi bigint; x bigint;
BEGIN
 CALL sp_project_create(1,1,'Платформа для фрілансерів','PostgreSQL backend та консольний клієнт',10000,current_date+60,a);
 CALL sp_project_set_status(1,a,'open');
 INSERT INTO project_skills VALUES (a,1),(a,2);
 CALL sp_proposal_create(2,a,'Реалізую модель і запити',8000,20,x);
 CALL sp_proposal_create(3,a,'Підготую інтерфейс',9000,25,x);
 CALL sp_project_create(1,2,'Дизайн сторінки','Чернетка для демонстрації',5000,current_date+60,b);
 CALL sp_project_create(1,3,'Завершена БД каталогу','Історичний завершений контракт',3000,current_date+60,c);
 CALL sp_project_set_status(1,c,'open');
 CALL sp_proposal_create(2,c,'Розроблю каталог',3000,10,pr);
 CALL sp_proposal_accept(1,pr);
 CALL sp_contract_create(1,pr,co);
 CALL sp_milestone_create(1,co,'Схема й SQL',3000,current_date+30,mi);
 CALL sp_project_set_status(1,c,'in_progress');
 CALL sp_submission_create(2,mi,'SQL-скрипти каталогу','https://example.com/catalog',x);
 CALL sp_milestone_approve(1,mi);
 CALL sp_payment_register(1,mi,3000,'seed-catalog-payment',x);
 CALL sp_contract_complete(1,co);
 CALL sp_review_create(1,co,5::smallint,'Робота виконана вчасно',x);
 CALL sp_review_create(2,co,5::smallint,'Чіткі вимоги',x);
 CALL sp_message_send(1,co,2,'Дякую за роботу',x);
 CALL sp_portfolio_create(2,'Каталог товарів','Реляційна модель каталогу','https://example.com/catalog',x);
 CALL sp_portfolio_create(3,'Архівний макет','Прихований приклад','https://example.com/design',x);
 CALL sp_portfolio_soft_delete(3,x);
 CALL sp_project_create(4,1,'Прихований проєкт','Soft delete приклад',2000,current_date+60,x);
 CALL sp_project_soft_delete(4,x);
END $$;
COMMIT;
